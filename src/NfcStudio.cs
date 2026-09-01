using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace NfcStudio
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--self-test")
            {
                Console.WriteLine(SelfTest.Run());
                return;
            }
            if (args.Length > 0 && args[0] == "--reader-test")
            {
                Console.WriteLine(SelfTest.ReaderTest());
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    // Valeurs explicites pour préserver la compatibilité des sauvegardes V1.
    internal enum CardKind { None = 0, Classic1K = 1, Ntag213 = 2, Unknown = 3, Ntag215 = 4, Ntag216 = 5 }
    internal enum DataFormat { Text, Url, Hex }

    internal sealed class CardInfo
    {
        public CardKind Kind;
        public byte[] Uid = new byte[0];
        public byte[] Atr = new byte[0];
        public string DisplayType = "Aucune puce";
        public bool KnownProfile;
        public bool HasNtagLocks;
        public int UserBytes;
        public int UserFirstPage;
        public int UserLastPage;
        public int DynamicLockPage;
        public int LastReadablePage;
        public bool IsNtag { get { return Kind == CardKind.Ntag213 || Kind == CardKind.Ntag215 || Kind == CardKind.Ntag216; } }
        public string UidText { get { return Hex.Join(Uid, ":"); } }
    }

    internal sealed class CardDump
    {
        public CardInfo Info;
        public SortedDictionary<int, byte[]> Units = new SortedDictionary<int, byte[]>();
        public List<int> Inaccessible = new List<int>();
    }

    internal static class NtagProfile
    {
        public static bool ConfigureFromCapabilityContainer(CardInfo info, byte[] cc)
        {
            if (cc == null || cc.Length < 4 || cc[0] != 0xE1 || cc[1] != 0x10) return false;
            if (cc[2] == 0x12) info.Kind = CardKind.Ntag213;
            else if (cc[2] == 0x3E) info.Kind = CardKind.Ntag215;
            else if (cc[2] == 0x6D) info.Kind = CardKind.Ntag216;
            else return false;
            Configure(info);
            return true;
        }

        public static void Configure(CardInfo info)
        {
            if (info == null) return;
            info.UserFirstPage = 4;
            if (info.Kind == CardKind.Ntag213)
            {
                info.DisplayType = "NTAG213 / compatible"; info.UserBytes = 144; info.UserLastPage = 39; info.DynamicLockPage = 40; info.LastReadablePage = 44;
            }
            else if (info.Kind == CardKind.Ntag215)
            {
                info.DisplayType = "NTAG215 / compatible"; info.UserBytes = 504; info.UserLastPage = 129; info.DynamicLockPage = 130; info.LastReadablePage = 134;
            }
            else if (info.Kind == CardKind.Ntag216)
            {
                info.DisplayType = "NTAG216 / compatible"; info.UserBytes = 888; info.UserLastPage = 225; info.DynamicLockPage = 226; info.LastReadablePage = 230;
            }
        }
    }

    internal sealed class MainForm : Form
    {
        private readonly Color Navy = Color.FromArgb(21, 35, 61);
        private readonly Color Blue = Color.FromArgb(43, 108, 176);
        private readonly Color Pale = Color.FromArgb(242, 246, 252);
        private readonly Color Danger = Color.FromArgb(198, 40, 40);
        private readonly Color Good = Color.FromArgb(28, 130, 82);

        private Pcsc pcsc;
        private Timer pollTimer;
        private CardInfo current;
        private CardDump lastDump;
        private string lastUid = "";
        private bool busy;

        private Label readerState, cardType, uidValue, capacityValue, accessValue, validation, counter;
        private ComboBox formatBox, keyTypeBox;
        private TextBox keyBox;
        private RichTextBox editor, rawView, logView;
        private Button writeButton, resetButton, readButton, backupButton, restoreButton;
        private Panel editorBorder;

        public MainForm()
        {
            Text = "NFC Studio — ACR122";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(980, 680);
            Size = new Size(1120, 760);
            Font = new Font("Segoe UI", 9.5f);
            BackColor = Pale;
            BuildUi();

            try
            {
                pcsc = new Pcsc();
                List<string> readers = pcsc.ListReaders();
                string selected = readers.Find(delegate(string s) { return s.IndexOf("ACR122", StringComparison.OrdinalIgnoreCase) >= 0; });
                if (selected == null && readers.Count > 0) selected = readers[0];
                pcsc.ReaderName = selected;
                readerState.Text = selected == null ? "Lecteur introuvable" : selected + " — prêt";
                readerState.ForeColor = selected == null ? Danger : Good;
                Log(selected == null ? "Aucun lecteur PC/SC détecté." : "Lecteur connecté : " + selected);
            }
            catch (Exception ex)
            {
                readerState.Text = "Erreur PC/SC";
                readerState.ForeColor = Danger;
                Log("Erreur d'initialisation : " + ex.Message);
            }

            pollTimer = new Timer();
            pollTimer.Interval = 650;
            pollTimer.Tick += PollTimer_Tick;
            pollTimer.Start();
            FormClosed += delegate { if (pcsc != null) pcsc.Dispose(); };
            UpdateValidation();
        }

        private void BuildUi()
        {
            Panel header = new Panel { Dock = DockStyle.Top, Height = 82, BackColor = Navy, Padding = new Padding(24, 14, 24, 12) };
            Label title = new Label { Text = "NFC Studio", ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 20f), AutoSize = true, Location = new Point(22, 10) };
            readerState = new Label { Text = "Recherche du lecteur…", ForeColor = Color.LightGray, AutoSize = true, Location = new Point(25, 52) };
            header.Controls.Add(title); header.Controls.Add(readerState);

            Panel info = new Panel { Dock = DockStyle.Left, Width = 285, BackColor = Color.White, Padding = new Padding(20) };
            int y = 20;
            info.Controls.Add(MakeHeading("Puce détectée", 20, y)); y += 42;
            info.Controls.Add(MakeCaption("TYPE", 20, y)); y += 22;
            cardType = MakeValue("Aucune puce", 20, y, 245); info.Controls.Add(cardType); y += 56;
            info.Controls.Add(MakeCaption("UID", 20, y)); y += 22;
            uidValue = MakeValue("—", 20, y, 245); info.Controls.Add(uidValue); y += 52;
            info.Controls.Add(MakeCaption("CAPACITÉ D'ÉCRITURE", 20, y)); y += 22;
            capacityValue = MakeValue("—", 20, y, 245); info.Controls.Add(capacityValue); y += 52;
            info.Controls.Add(MakeCaption("ACCÈS", 20, y)); y += 22;
            accessValue = MakeValue("—", 20, y, 245); info.Controls.Add(accessValue); y += 61;

            Label keyLabel = MakeCaption("CLÉ CLASSIC (12 HEX)", 20, y); info.Controls.Add(keyLabel); y += 23;
            keyBox = new TextBox { Text = "FFFFFFFFFFFF", Location = new Point(20, y), Width = 155, CharacterCasing = CharacterCasing.Upper };
            keyTypeBox = new ComboBox { Location = new Point(181, y), Width = 64, DropDownStyle = ComboBoxStyle.DropDownList };
            keyTypeBox.Items.AddRange(new object[] { "A", "B" }); keyTypeBox.SelectedIndex = 0;
            info.Controls.Add(keyBox); info.Controls.Add(keyTypeBox); y += 48;
            Label note = new Label { Text = "La clé reste uniquement en mémoire pendant l'utilisation.", ForeColor = Color.DimGray, Location = new Point(20, y), Width = 235, Height = 48 };
            info.Controls.Add(note);
            Panel body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(22, 18, 22, 18), BackColor = Pale };
            TabControl tabs = new TabControl { Dock = DockStyle.Fill };
            TabPage editTab = new TabPage("Contenu") { BackColor = Color.White, Padding = new Padding(18) };
            TabPage rawTab = new TabPage("Mémoire brute") { BackColor = Color.White, Padding = new Padding(12) };
            TabPage logTab = new TabPage("Journal") { BackColor = Color.White, Padding = new Padding(12) };

            Panel editTop = new Panel { Dock = DockStyle.Top, Height = 64 };
            Label fmtLabel = MakeCaption("FORMAT D'ÉCRITURE", 0, 0); editTop.Controls.Add(fmtLabel);
            formatBox = new ComboBox { Location = new Point(0, 25), Width = 230, DropDownStyle = ComboBoxStyle.DropDownList };
            formatBox.Items.AddRange(new object[] { "Texte UTF-8", "URL NFC", "Hexadécimal brut" }); formatBox.SelectedIndex = 0;
            editTop.Controls.Add(formatBox);
            counter = new Label { Text = "0 octet", TextAlign = ContentAlignment.MiddleRight, Dock = DockStyle.Right, Width = 250, Padding = new Padding(0, 25, 0, 0) };
            editTop.Controls.Add(counter);

            editorBorder = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(205, 213, 224), Padding = new Padding(2) };
            editor = new RichTextBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, Font = new Font("Consolas", 11f), AcceptsTab = true, BackColor = Color.White };
            editorBorder.Controls.Add(editor);
            validation = new Label { Dock = DockStyle.Bottom, Height = 48, ForeColor = Color.DimGray, Padding = new Padding(0, 8, 0, 0) };

            FlowLayoutPanel buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(0, 6, 0, 0) };
            readButton = MakeButton("Relire", false); writeButton = MakeButton("Écrire", true); backupButton = MakeButton("Sauvegarder", false); restoreButton = MakeButton("Restaurer", false); resetButton = MakeButton("Réinitialiser", false);
            buttons.Controls.Add(readButton); buttons.Controls.Add(writeButton); buttons.Controls.Add(backupButton); buttons.Controls.Add(restoreButton); buttons.Controls.Add(resetButton);
            editTab.Controls.Add(editorBorder); editTab.Controls.Add(validation); editTab.Controls.Add(buttons); editTab.Controls.Add(editTop);

            rawView = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, Font = new Font("Consolas", 10f), BackColor = Color.FromArgb(249, 250, 252), BorderStyle = BorderStyle.None };
            logView = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, Font = new Font("Consolas", 9.5f), BackColor = Color.FromArgb(249, 250, 252), BorderStyle = BorderStyle.None };
            rawTab.Controls.Add(rawView); logTab.Controls.Add(logView);
            tabs.TabPages.Add(editTab); tabs.TabPages.Add(rawTab); tabs.TabPages.Add(logTab);
            body.Controls.Add(tabs);
            // Ordre de docking WinForms : Fill d'abord, puis côtés, puis en-tête.
            Controls.Add(body); Controls.Add(info); Controls.Add(header);

            formatBox.SelectedIndexChanged += delegate { UpdateValidation(); };
            editor.TextChanged += delegate { UpdateValidation(); };
            keyBox.TextChanged += delegate { UpdateValidation(); };
            keyTypeBox.SelectedIndexChanged += delegate { lastUid = ""; };
            readButton.Click += delegate { ReadCurrent(true); };
            writeButton.Click += WriteButton_Click;
            resetButton.Click += ResetButton_Click;
            backupButton.Click += BackupButton_Click;
            restoreButton.Click += RestoreButton_Click;
        }

        private Label MakeHeading(string text, int x, int y) { return new Label { Text = text, Font = new Font("Segoe UI Semibold", 14f), ForeColor = Navy, AutoSize = true, Location = new Point(x, y) }; }
        private Label MakeCaption(string text, int x, int y) { return new Label { Text = text, Font = new Font("Segoe UI Semibold", 8f), ForeColor = Color.FromArgb(95, 105, 120), AutoSize = true, Location = new Point(x, y) }; }
        private Label MakeValue(string text, int x, int y, int width) { return new Label { Text = text, Font = new Font("Segoe UI Semibold", 10.5f), ForeColor = Navy, Location = new Point(x, y), Width = width, Height = 40 }; }
        private Button MakeButton(string text, bool primary)
        {
            Button b = new Button { Text = text, AutoSize = false, Width = text.Length > 11 ? 126 : 103, Height = 36, Margin = new Padding(0, 0, 8, 0), FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand };
            b.FlatAppearance.BorderSize = primary ? 0 : 1; b.BackColor = primary ? Blue : Color.White; b.ForeColor = primary ? Color.White : Navy;
            return b;
        }

        private void PollTimer_Tick(object sender, EventArgs e)
        {
            if (busy || pcsc == null || pcsc.ReaderName == null) return;
            try
            {
                CardInfo info = pcsc.Detect();
                string uid = info == null ? "" : info.UidText;
                if (uid != lastUid)
                {
                    lastUid = uid;
                    current = info;
                    if (info == null) { ClearCard(); Log("Puce retirée."); }
                    else { ShowCard(info); Log("Puce détectée : " + info.DisplayType + " — UID " + info.UidText); ReadCurrent(false); }
                }
            }
            catch (Exception ex) { Log("Détection : " + ex.Message); }
        }

        private void ClearCard()
        {
            current = null; lastDump = null; cardType.Text = "Aucune puce"; uidValue.Text = "—"; capacityValue.Text = "—"; accessValue.Text = "—"; rawView.Clear(); UpdateValidation();
        }

        private void ShowCard(CardInfo info)
        {
            cardType.Text = info.DisplayType; uidValue.Text = info.UidText;
            if (info.IsNtag) { capacityValue.Text = info.UserBytes + " octets utilisateur"; accessValue.Text = info.HasNtagLocks ? "Verrouillage détecté" : "Lecture / écriture"; }
            else if (info.Kind == CardKind.Classic1K) { capacityValue.Text = "720 appli. / 752 physiques"; accessValue.Text = "Clé requise par secteur"; }
            else { capacityValue.Text = "Type non pris en charge"; accessValue.Text = "Lecture UID uniquement"; }
            UpdateValidation();
        }

        private byte[] CurrentKey()
        {
            byte[] result;
            return Hex.TryParse(keyBox.Text, out result) && result.Length == 6 ? result : null;
        }

        private void ReadCurrent(bool showErrors)
        {
            if (current == null || busy) return;
            busy = true; UseWaitCursor = true;
            try
            {
                byte[] key = CurrentKey();
                if (current.Kind == CardKind.Classic1K && key == null) throw new InvalidOperationException("La clé Classic doit contenir exactement 12 caractères hexadécimaux.");
                lastDump = pcsc.ReadDump(current, key, keyTypeBox.SelectedIndex == 0);
                rawView.Text = FormatDump(lastDump);
                LoadEditorFromDump(lastDump);
                if (current.Kind == CardKind.Classic1K)
                {
                    int goodSectors = 16 - lastDump.Inaccessible.Count;
                    accessValue.Text = string.Format("{0}/16 secteurs accessibles", goodSectors);
                }
                Log("Lecture terminée sans modification.");
            }
            catch (Exception ex) { Log("Erreur de lecture : " + ex.Message); if (showErrors) MessageBox.Show(this, ex.Message, "Lecture impossible", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            finally { busy = false; UseWaitCursor = false; UpdateValidation(); }
        }

        private string FormatDump(CardDump dump)
        {
            StringBuilder s = new StringBuilder();
            s.AppendLine("Type : " + dump.Info.DisplayType); s.AppendLine("UID  : " + dump.Info.UidText); s.AppendLine("ATR  : " + Hex.Join(dump.Info.Atr, " ")); s.AppendLine();
            foreach (KeyValuePair<int, byte[]> kv in dump.Units)
            {
                string label = dump.Info.IsNtag ? "Page " + kv.Key.ToString("000") : "Bloc " + kv.Key.ToString("00");
                s.Append(label.PadRight(9)).Append("  ").Append(Hex.Join(kv.Value, " ")).Append("   ").Append(Hex.Ascii(kv.Value)).AppendLine();
            }
            if (dump.Inaccessible.Count > 0) { s.AppendLine(); s.AppendLine("Inaccessibles : " + string.Join(", ", dump.Inaccessible.ConvertAll<string>(delegate(int i) { return i.ToString(); }).ToArray())); }
            return s.ToString();
        }

        private void LoadEditorFromDump(CardDump dump)
        {
            string value; DataFormat fmt;
            if (dump.Info.IsNtag)
            {
                byte[] user = Collect(dump, dump.Info.UserFirstPage, dump.Info.UserLastPage);
                if (Ndef.TryDecode(user, out fmt, out value)) { formatBox.SelectedIndex = (int)fmt; editor.Text = value; }
                else { formatBox.SelectedIndex = 2; editor.Text = Hex.Join(Hex.TrimTrailingZeros(user), " "); }
            }
            else if (dump.Info.Kind == CardKind.Classic1K)
            {
                byte[] app = CollectClassicApp(dump);
                if (AppEnvelope.TryDecode(app, out fmt, out value)) { formatBox.SelectedIndex = (int)fmt; editor.Text = value; }
                else { formatBox.SelectedIndex = 2; editor.Text = Hex.Join(Hex.TrimTrailingZeros(app), " "); }
            }
        }

        private static byte[] Collect(CardDump dump, int first, int last)
        {
            List<byte> all = new List<byte>();
            for (int i = first; i <= last; i++) if (dump.Units.ContainsKey(i)) all.AddRange(dump.Units[i]);
            return all.ToArray();
        }

        private static byte[] CollectClassicApp(CardDump dump)
        {
            List<byte> all = new List<byte>();
            for (int block = 4; block < 64; block++) if ((block + 1) % 4 != 0 && dump.Units.ContainsKey(block)) all.AddRange(dump.Units[block]);
            return all.ToArray();
        }

        private DataFormat SelectedFormat { get { return (DataFormat)Math.Max(0, formatBox.SelectedIndex); } }

        private ValidationResult ValidateInput()
        {
            if (current == null) return ValidationResult.Fail("Pose une puce sur le lecteur.", 0, 0);
            if (!current.IsNtag && current.Kind != CardKind.Classic1K) return ValidationResult.Fail("Ce type de puce n'est pas pris en charge en écriture.", 0, 0);
            if (current.Kind == CardKind.Classic1K && CurrentKey() == null) return ValidationResult.Fail("La clé Classic doit contenir exactement 12 caractères hexadécimaux.", 0, 0);
            byte[] data;
            if (SelectedFormat == DataFormat.Hex)
            {
                if (!Hex.TryParse(editor.Text, out data)) return ValidationResult.Fail("Hexadécimal invalide : utilise uniquement 0–9 et A–F, avec un nombre pair de chiffres.", 0, current.IsNtag ? current.UserBytes : 720);
            }
            else data = Encoding.UTF8.GetBytes(editor.Text);

            if (SelectedFormat == DataFormat.Url)
            {
                Uri uri;
                if (!Uri.TryCreate(editor.Text, UriKind.Absolute, out uri) || (uri.Scheme != "http" && uri.Scheme != "https")) return ValidationResult.Fail("L'URL doit être complète et commencer par http:// ou https://", data.Length, 0);
            }
            foreach (char c in editor.Text) if (c < 32 && c != '\r' && c != '\n' && c != '\t') return ValidationResult.Fail("Le contenu contient un caractère de contrôle non autorisé.", data.Length, 0);

            try
            {
                byte[] encoded;
                int limit;
                if (current.IsNtag) { encoded = SelectedFormat == DataFormat.Hex ? data : Ndef.Encode(SelectedFormat, editor.Text); limit = current.UserBytes; }
                else { encoded = SelectedFormat == DataFormat.Hex ? data : AppEnvelope.Encode(SelectedFormat, editor.Text); limit = 720; }
                if (encoded.Length > limit) return ValidationResult.Fail(string.Format("Contenu trop long : {0} octets nécessaires, {1} disponibles.", encoded.Length, limit), encoded.Length, limit);
                string hint = SelectedFormat == DataFormat.Hex ? "Données hexadécimales valides." : "Chiffres, accents et caractères spéciaux acceptés en UTF-8.";
                return ValidationResult.Ok(hint, encoded.Length, limit);
            }
            catch (Exception ex) { return ValidationResult.Fail(ex.Message, data.Length, 0); }
        }

        private void UpdateValidation()
        {
            if (validation == null) return;
            ValidationResult v = ValidateInput();
            validation.Text = v.Message; validation.ForeColor = v.Valid ? Good : Danger; editorBorder.BackColor = v.Valid ? Color.FromArgb(205, 213, 224) : Danger;
            counter.Text = v.Limit > 0 ? string.Format("{0} / {1} octets", v.Bytes, v.Limit) : string.Format("{0} octet(s)", v.Bytes);
            writeButton.Enabled = v.Valid; resetButton.Enabled = current != null && (current.IsNtag || current.Kind == CardKind.Classic1K); backupButton.Enabled = lastDump != null; readButton.Enabled = current != null;
        }

        private void WriteButton_Click(object sender, EventArgs e)
        {
            ValidationResult v = ValidateInput(); if (!v.Valid) { MessageBox.Show(this, v.Message, "Contenu invalide", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            if (MessageBox.Show(this, "Une sauvegarde automatique sera créée avant l'écriture. Continuer ?", "Confirmer l'écriture", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            RunWrite(false);
        }

        private void ResetButton_Click(object sender, EventArgs e)
        {
            if (current == null) return;
            string text = "La zone utilisateur sera effacée. L'UID, les données fabricant, les clés Classic et les réglages de sécurité seront conservés. Une sauvegarde automatique sera créée. Continuer ?";
            if (MessageBox.Show(this, text, "Réinitialiser la puce", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            RunWrite(true);
        }

        private void RunWrite(bool reset)
        {
            busy = true; UseWaitCursor = true;
            try
            {
                CardDump original = pcsc.ReadDump(current, CurrentKey(), keyTypeBox.SelectedIndex == 0);
                string backupPath = BackupFile.AutoPath(current);
                BackupFile.Save(backupPath, original);
                Log("Sauvegarde automatique : " + backupPath);
                if (current.IsNtag)
                {
                    if (current.HasNtagLocks) throw new InvalidOperationException("Des bits de verrouillage sont actifs. L'écriture automatique est bloquée pour éviter une modification partielle.");
                    byte[] payload;
                    if (reset) payload = new byte[] { 0x03, 0x00, 0xFE };
                    else if (SelectedFormat == DataFormat.Hex) { if (!Hex.TryParse(editor.Text, out payload)) throw new InvalidOperationException("Hexadécimal invalide."); }
                    else payload = Ndef.Encode(SelectedFormat, editor.Text);
                    pcsc.WriteNtagUser(current, payload);
                }
                else
                {
                    byte[] payload;
                    if (reset) payload = new byte[0];
                    else if (SelectedFormat == DataFormat.Hex) { if (!Hex.TryParse(editor.Text, out payload)) throw new InvalidOperationException("Hexadécimal invalide."); }
                    else payload = AppEnvelope.Encode(SelectedFormat, editor.Text);
                    if (reset) pcsc.ResetClassicUser(current, CurrentKey(), keyTypeBox.SelectedIndex == 0);
                    else pcsc.WriteClassicApp(current, CurrentKey(), keyTypeBox.SelectedIndex == 0, payload);
                }
                lastDump = pcsc.ReadDump(current, CurrentKey(), keyTypeBox.SelectedIndex == 0);
                rawView.Text = FormatDump(lastDump); LoadEditorFromDump(lastDump);
                Log(reset ? "Réinitialisation terminée et vérifiée." : "Écriture terminée et vérifiée.");
                MessageBox.Show(this, reset ? "La zone utilisateur a été réinitialisée." : "Le contenu a été écrit et relu avec succès.", "Opération réussie", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { Log("Erreur d'écriture : " + ex.Message); MessageBox.Show(this, ex.Message, "Écriture impossible", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            finally { busy = false; UseWaitCursor = false; UpdateValidation(); }
        }

        private void BackupButton_Click(object sender, EventArgs e)
        {
            if (lastDump == null) return;
            SaveFileDialog d = new SaveFileDialog { Filter = "Sauvegarde NFC Studio (*.nfcbak)|*.nfcbak", FileName = BackupFile.SuggestedName(current) };
            if (d.ShowDialog(this) == DialogResult.OK) { BackupFile.Save(d.FileName, lastDump); Log("Sauvegarde créée : " + d.FileName); }
        }

        private void RestoreButton_Click(object sender, EventArgs e)
        {
            if (current == null) { MessageBox.Show(this, "Pose d'abord la puce à restaurer."); return; }
            OpenFileDialog d = new OpenFileDialog { Filter = "Sauvegarde NFC Studio (*.nfcbak)|*.nfcbak" };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                CardDump backup = BackupFile.Load(d.FileName);
                if (backup.Info.Kind != current.Kind) throw new InvalidOperationException("La sauvegarde ne correspond pas au type de puce posé.");
                if (backup.Info.UidText != current.UidText && MessageBox.Show(this, "L'UID de la sauvegarde est différent. Restaurer quand même les données utilisateur ?", "UID différent", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                if (MessageBox.Show(this, "Restaurer les données utilisateur sauvegardées ? Les zones fabricant et sécurité seront préservées.", "Confirmer", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                busy = true; UseWaitCursor = true;
                if (current.IsNtag)
                {
                    NtagProfile.Configure(backup.Info);
                    if (backup.Info.UserBytes != current.UserBytes) throw new InvalidOperationException("La sauvegarde NTAG n'a pas la même capacité que la puce posée.");
                    pcsc.WriteNtagUser(current, Collect(backup, backup.Info.UserFirstPage, backup.Info.UserLastPage));
                }
                else pcsc.RestoreClassicUser(current, CurrentKey(), keyTypeBox.SelectedIndex == 0, backup);
                lastDump = pcsc.ReadDump(current, CurrentKey(), keyTypeBox.SelectedIndex == 0);
                rawView.Text = FormatDump(lastDump); LoadEditorFromDump(lastDump);
                Log("Restauration terminée : " + d.FileName); MessageBox.Show(this, "Restauration réussie.", "NFC Studio", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { Log("Erreur de restauration : " + ex.Message); MessageBox.Show(this, ex.Message, "Restauration impossible", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            finally { busy = false; UseWaitCursor = false; }
        }

        private void Log(string text)
        {
            if (logView == null) return;
            logView.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + text + Environment.NewLine); logView.ScrollToCaret();
        }
    }

    internal sealed class ValidationResult
    {
        public bool Valid; public string Message; public int Bytes; public int Limit;
        public static ValidationResult Ok(string m, int b, int l) { return new ValidationResult { Valid = true, Message = m, Bytes = b, Limit = l }; }
        public static ValidationResult Fail(string m, int b, int l) { return new ValidationResult { Valid = false, Message = m, Bytes = b, Limit = l }; }
    }

    internal sealed class Pcsc : IDisposable
    {
        private const uint SCARD_SCOPE_SYSTEM = 2, SCARD_SHARE_SHARED = 2, SCARD_PROTOCOL_T0 = 1, SCARD_PROTOCOL_T1 = 2, SCARD_LEAVE_CARD = 0;
        private const int SCARD_E_NO_SMARTCARD = unchecked((int)0x8010000C), SCARD_W_REMOVED_CARD = unchecked((int)0x80100069), SCARD_E_READER_UNAVAILABLE = unchecked((int)0x80100017);
        private IntPtr context;
        public string ReaderName;

        [StructLayout(LayoutKind.Sequential)] private struct SCardIoRequest { public uint Protocol; public uint Length; }
        [DllImport("winscard.dll")] private static extern int SCardEstablishContext(uint scope, IntPtr r1, IntPtr r2, out IntPtr context);
        [DllImport("winscard.dll")] private static extern int SCardReleaseContext(IntPtr context);
        [DllImport("winscard.dll", CharSet = CharSet.Unicode)] private static extern int SCardListReaders(IntPtr context, string groups, char[] readers, ref uint length);
        [DllImport("winscard.dll", CharSet = CharSet.Unicode)] private static extern int SCardConnect(IntPtr context, string reader, uint share, uint protocols, out IntPtr card, out uint activeProtocol);
        [DllImport("winscard.dll")] private static extern int SCardDisconnect(IntPtr card, uint disposition);
        [DllImport("winscard.dll")] private static extern int SCardTransmit(IntPtr card, ref SCardIoRequest sendPci, byte[] send, uint sendLength, IntPtr recvPci, byte[] recv, ref uint recvLength);
        [DllImport("winscard.dll", CharSet = CharSet.Unicode)] private static extern int SCardStatus(IntPtr card, StringBuilder reader, ref uint readerLength, out uint state, out uint protocol, byte[] atr, ref uint atrLength);

        public Pcsc() { int rc = SCardEstablishContext(SCARD_SCOPE_SYSTEM, IntPtr.Zero, IntPtr.Zero, out context); Check(rc, "Initialisation PC/SC"); }
        public void Dispose() { if (context != IntPtr.Zero) { SCardReleaseContext(context); context = IntPtr.Zero; } }

        public List<string> ListReaders()
        {
            uint n = 0; int rc = SCardListReaders(context, null, null, ref n); if (rc != 0) return new List<string>();
            char[] buf = new char[n]; Check(SCardListReaders(context, null, buf, ref n), "Liste des lecteurs");
            return new List<string>(new string(buf).Split(new char[] { '\0' }, StringSplitOptions.RemoveEmptyEntries));
        }

        public CardInfo Detect()
        {
            if (ReaderName == null) return null;
            using (CardSession s = Connect(false))
            {
                if (s == null) return null;
                byte[] uid = Data(s.Transmit(new byte[] { 0xFF, 0xCA, 0, 0, 0 }), "Lecture UID");
                byte[] atr = s.GetAtr();
                CardInfo info = new CardInfo { Uid = uid, Atr = atr, Kind = Identify(atr) };
                if (info.Kind == CardKind.Ntag213)
                {
                    byte[] cc = Data(s.Transmit(new byte[] { 0xFF, 0xB0, 0, 3, 4 }), "Lecture CC");
                    if (!NtagProfile.ConfigureFromCapabilityContainer(info, cc))
                    {
                        info.Kind = CardKind.Unknown;
                        info.DisplayType = "Tag NFC Type 2 inconnu";
                    }
                    if (info.IsNtag)
                    {
                        byte[] p2 = Data(s.Transmit(new byte[] { 0xFF, 0xB0, 0, 2, 4 }), "Verrous statiques");
                        byte[] dynamicLocks = Data(s.Transmit(new byte[] { 0xFF, 0xB0, 0, (byte)info.DynamicLockPage, 4 }), "Verrous dynamiques");
                        info.HasNtagLocks = p2.Length >= 4 && (p2[2] != 0 || p2[3] != 0) || dynamicLocks.Length >= 3 && (dynamicLocks[0] != 0 || dynamicLocks[1] != 0 || dynamicLocks[2] != 0);
                    }
                }
                else if (info.Kind == CardKind.Classic1K)
                {
                    if (info.UidText == "60:BC:5B:9B") { info.DisplayType = "Fudan FM1108 (profil connu)"; info.KnownProfile = true; }
                    else if (info.UidText == "13:CA:69:0E") { info.DisplayType = "MIFARE Classic 1K (profil connu)"; info.KnownProfile = true; }
                    else info.DisplayType = "MIFARE Classic 1K compatible";
                }
                else info.DisplayType = "Puce ISO 14443 inconnue";
                return info;
            }
        }

        private static CardKind Identify(byte[] atr)
        {
            string h = Hex.Join(atr, "");
            if (h.IndexOf("000100000000", StringComparison.Ordinal) >= 0) return CardKind.Classic1K;
            if (h.IndexOf("000300000000", StringComparison.Ordinal) >= 0) return CardKind.Ntag213;
            return CardKind.Unknown;
        }

        public CardDump ReadDump(CardInfo info, byte[] key, bool keyA)
        {
            CardDump dump = new CardDump { Info = info };
            using (CardSession s = Connect(true))
            {
                EnsureSameUid(s, info);
                if (info.IsNtag)
                {
                    NtagProfile.Configure(info);
                    for (int page = 0; page <= info.LastReadablePage; page++) dump.Units[page] = Data(s.Transmit(new byte[] { 0xFF, 0xB0, 0, (byte)page, 4 }), "Lecture page " + page);
                }
                else if (info.Kind == CardKind.Classic1K)
                {
                    if (key == null || key.Length != 6) throw new InvalidOperationException("Clé Classic invalide.");
                    LoadKey(s, key);
                    for (int sector = 0; sector < 16; sector++)
                    {
                        int first = sector * 4;
                        if (!Authenticate(s, first, keyA)) { dump.Inaccessible.Add(sector); continue; }
                        for (int b = first; b < first + 4; b++)
                        {
                            ApduResponse r = s.Transmit(new byte[] { 0xFF, 0xB0, 0, (byte)b, 16 });
                            if (r.Success) dump.Units[b] = r.Data;
                        }
                    }
                }
                else throw new InvalidOperationException("Type de puce non pris en charge.");
            }
            return dump;
        }

        public void WriteNtagUser(CardInfo info, byte[] payload)
        {
            NtagProfile.Configure(info);
            if (payload.Length > info.UserBytes) throw new InvalidOperationException("Le contenu dépasse " + info.UserBytes + " octets.");
            byte[] area = new byte[info.UserBytes]; Array.Copy(payload, area, payload.Length);
            using (CardSession s = Connect(true))
            {
                EnsureSameUid(s, info);
                // Page 4 neutralisée d'abord : évite qu'un téléphone lise un message partiellement écrit.
                WriteUnit(s, 4, new byte[] { 0x03, 0x00, 0xFE, 0x00 });
                for (int page = 5; page <= info.UserLastPage; page++) { byte[] p = new byte[4]; Array.Copy(area, (page - info.UserFirstPage) * 4, p, 0, 4); WriteUnit(s, page, p); }
                byte[] p4 = new byte[4]; Array.Copy(area, 0, p4, 0, 4); WriteUnit(s, 4, p4);
                for (int page = info.UserFirstPage; page <= info.UserLastPage; page++)
                {
                    byte[] got = Data(s.Transmit(new byte[] { 0xFF, 0xB0, 0, (byte)page, 4 }), "Vérification page " + page);
                    for (int i = 0; i < 4; i++) if (got[i] != area[(page - info.UserFirstPage) * 4 + i]) throw new IOException("Échec de vérification à la page " + page + ".");
                }
            }
        }

        public void WriteClassicApp(CardInfo info, byte[] key, bool keyA, byte[] payload)
        {
            if (payload.Length > 720) throw new InvalidOperationException("Le contenu dépasse 720 octets.");
            byte[] area = new byte[720]; Array.Copy(payload, area, payload.Length); int offset = 0;
            using (CardSession s = Connect(true))
            {
                EnsureSameUid(s, info); LoadKey(s, key);
                for (int sector = 1; sector < 16; sector++)
                {
                    int first = sector * 4; if (!Authenticate(s, first, keyA)) throw new UnauthorizedAccessException("Secteur " + sector + " inaccessible avec cette clé.");
                    for (int b = first; b < first + 3; b++) { byte[] block = new byte[16]; Array.Copy(area, offset, block, 0, 16); WriteUnit(s, b, block); offset += 16; }
                }
                VerifyClassicArea(s, key, keyA, area);
            }
        }

        public void ResetClassicUser(CardInfo info, byte[] key, bool keyA)
        {
            using (CardSession s = Connect(true))
            {
                EnsureSameUid(s, info); LoadKey(s, key);
                for (int sector = 0; sector < 16; sector++)
                {
                    int first = sector * 4; if (!Authenticate(s, first, keyA)) throw new UnauthorizedAccessException("Secteur " + sector + " inaccessible avec cette clé.");
                    int start = sector == 0 ? 1 : first;
                    for (int b = start; b < first + 3; b++) WriteUnit(s, b, new byte[16]);
                }
            }
        }

        public void RestoreClassicUser(CardInfo info, byte[] key, bool keyA, CardDump backup)
        {
            using (CardSession s = Connect(true))
            {
                EnsureSameUid(s, info); LoadKey(s, key);
                for (int sector = 0; sector < 16; sector++)
                {
                    int first = sector * 4; if (!Authenticate(s, first, keyA)) throw new UnauthorizedAccessException("Secteur " + sector + " inaccessible avec cette clé.");
                    int start = sector == 0 ? 1 : first;
                    for (int b = start; b < first + 3; b++)
                    {
                        if (!backup.Units.ContainsKey(b) || backup.Units[b].Length != 16) throw new InvalidDataException("Sauvegarde incomplète au bloc " + b + ".");
                        WriteUnit(s, b, backup.Units[b]);
                    }
                }
            }
        }

        private static byte[] CollectClassic(CardDump dump)
        {
            List<byte> data = new List<byte>();
            for (int b = 4; b < 64; b++) if ((b + 1) % 4 != 0) { if (!dump.Units.ContainsKey(b)) throw new InvalidDataException("Sauvegarde incomplète au bloc " + b + "."); data.AddRange(dump.Units[b]); }
            return data.ToArray();
        }

        private void VerifyClassicArea(CardSession s, byte[] key, bool keyA, byte[] expected)
        {
            int offset = 0;
            for (int sector = 1; sector < 16; sector++)
            {
                int first = sector * 4; if (!Authenticate(s, first, keyA)) throw new UnauthorizedAccessException("Vérification impossible au secteur " + sector + ".");
                for (int b = first; b < first + 3; b++)
                {
                    byte[] got = Data(s.Transmit(new byte[] { 0xFF, 0xB0, 0, (byte)b, 16 }), "Vérification bloc " + b);
                    for (int i = 0; i < 16; i++) if (got[i] != expected[offset + i]) throw new IOException("Échec de vérification au bloc " + b + "."); offset += 16;
                }
            }
        }

        private void LoadKey(CardSession s, byte[] key) { Data(s.Transmit(Hex.Concat(new byte[] { 0xFF, 0x82, 0, 0, 6 }, key)), "Chargement de la clé"); }
        private bool Authenticate(CardSession s, int block, bool keyA) { return s.Transmit(new byte[] { 0xFF, 0x86, 0, 0, 5, 1, 0, (byte)block, keyA ? (byte)0x60 : (byte)0x61, 0 }).Success; }
        private void WriteUnit(CardSession s, int address, byte[] data) { Data(s.Transmit(Hex.Concat(new byte[] { 0xFF, 0xD6, 0, (byte)address, (byte)data.Length }, data)), "Écriture unité " + address); }
        private void EnsureSameUid(CardSession s, CardInfo info) { byte[] got = Data(s.Transmit(new byte[] { 0xFF, 0xCA, 0, 0, 0 }), "Lecture UID"); if (Hex.Join(got, "") != Hex.Join(info.Uid, "")) throw new InvalidOperationException("La puce a changé pendant l'opération."); }

        private CardSession Connect(bool throwIfMissing)
        {
            IntPtr card; uint protocol; int rc = SCardConnect(context, ReaderName, SCARD_SHARE_SHARED, SCARD_PROTOCOL_T0 | SCARD_PROTOCOL_T1, out card, out protocol);
            if (rc == SCARD_E_NO_SMARTCARD || rc == SCARD_W_REMOVED_CARD || rc == SCARD_E_READER_UNAVAILABLE) { if (throwIfMissing) throw new InvalidOperationException("Aucune puce stable sur le lecteur."); return null; }
            Check(rc, "Connexion à la puce"); return new CardSession(card, protocol);
        }

        private static byte[] Data(ApduResponse r, string operation) { if (!r.Success) throw new IOException(operation + " refusée (code " + r.StatusText + ")."); return r.Data; }
        private static void Check(int rc, string op) { if (rc != 0) throw new InvalidOperationException(op + " — erreur PC/SC 0x" + rc.ToString("X8")); }

        private sealed class CardSession : IDisposable
        {
            private IntPtr card; private uint protocol;
            public CardSession(IntPtr c, uint p) { card = c; protocol = p; }
            public ApduResponse Transmit(byte[] command)
            {
                byte[] receive = new byte[258]; uint n = (uint)receive.Length; SCardIoRequest pci = new SCardIoRequest { Protocol = protocol, Length = (uint)Marshal.SizeOf(typeof(SCardIoRequest)) };
                int rc = SCardTransmit(card, ref pci, command, (uint)command.Length, IntPtr.Zero, receive, ref n); Check(rc, "Transmission");
                byte[] result = new byte[n]; Array.Copy(receive, result, n); return new ApduResponse(result);
            }
            public byte[] GetAtr()
            {
                StringBuilder name = new StringBuilder(256); uint nl = 256, state, p, al = 64; byte[] atr = new byte[64]; Check(SCardStatus(card, name, ref nl, out state, out p, atr, ref al), "Lecture ATR"); byte[] r = new byte[al]; Array.Copy(atr, r, al); return r;
            }
            public void Dispose() { if (card != IntPtr.Zero) { SCardDisconnect(card, SCARD_LEAVE_CARD); card = IntPtr.Zero; } }
        }
    }

    internal sealed class ApduResponse
    {
        public byte[] Data; public byte Sw1, Sw2;
        public ApduResponse(byte[] raw) { if (raw.Length < 2) { Data = raw; return; } Sw1 = raw[raw.Length - 2]; Sw2 = raw[raw.Length - 1]; Data = new byte[raw.Length - 2]; Array.Copy(raw, Data, Data.Length); }
        public bool Success { get { return Sw1 == 0x90 && Sw2 == 0; } }
        public string StatusText { get { return Sw1.ToString("X2") + Sw2.ToString("X2"); } }
    }

    internal static class Ndef
    {
        public static byte[] Encode(DataFormat format, string text)
        {
            byte[] payload; byte type;
            if (format == DataFormat.Text)
            {
                byte[] value = Encoding.UTF8.GetBytes(text); payload = new byte[3 + value.Length]; payload[0] = 2; payload[1] = (byte)'f'; payload[2] = (byte)'r'; Array.Copy(value, 0, payload, 3, value.Length); type = (byte)'T';
            }
            else if (format == DataFormat.Url)
            {
                byte prefix = 0; string rest = text;
                string[] prefixes = { "", "http://www.", "https://www.", "http://", "https://" };
                for (int i = 1; i < prefixes.Length; i++) if (text.StartsWith(prefixes[i], StringComparison.OrdinalIgnoreCase)) { prefix = (byte)i; rest = text.Substring(prefixes[i].Length); break; }
                byte[] value = Encoding.UTF8.GetBytes(rest); payload = new byte[1 + value.Length]; payload[0] = prefix; Array.Copy(value, 0, payload, 1, value.Length); type = (byte)'U';
            }
            else throw new InvalidOperationException("Le format hexadécimal n'utilise pas NDEF.");
            bool shortRecord = payload.Length <= 255;
            int recordHeader = shortRecord ? 4 : 7;
            byte[] record = new byte[recordHeader + payload.Length];
            record[0] = shortRecord ? (byte)0xD1 : (byte)0xC1; // MB + ME + SR éventuel + TNF Well Known
            record[1] = 1;
            int payloadStart;
            if (shortRecord)
            {
                record[2] = (byte)payload.Length; record[3] = type; payloadStart = 4;
            }
            else
            {
                record[2] = (byte)(payload.Length >> 24); record[3] = (byte)(payload.Length >> 16);
                record[4] = (byte)(payload.Length >> 8); record[5] = (byte)payload.Length;
                record[6] = type; payloadStart = 7;
            }
            Array.Copy(payload, 0, record, payloadStart, payload.Length);

            bool extendedTlv = record.Length >= 255;
            int tlvHeader = extendedTlv ? 4 : 2;
            byte[] tlv = new byte[tlvHeader + record.Length + 1]; tlv[0] = 0x03;
            if (extendedTlv)
            {
                tlv[1] = 0xFF; tlv[2] = (byte)(record.Length >> 8); tlv[3] = (byte)record.Length;
            }
            else tlv[1] = (byte)record.Length;
            Array.Copy(record, 0, tlv, tlvHeader, record.Length); tlv[tlv.Length - 1] = 0xFE; return tlv;
        }

        public static bool TryDecode(byte[] area, out DataFormat format, out string value)
        {
            format = DataFormat.Hex; value = ""; int pos = 0, nlen = 0, r = 0;
            while (pos < area.Length)
            {
                if (area[pos] == 0) { pos++; continue; }
                if (area[pos] == 0xFE) return false;
                if (pos + 1 >= area.Length) return false;
                int header = 2; int len = area[pos + 1];
                if (len == 0xFF)
                {
                    if (pos + 3 >= area.Length) return false;
                    len = area[pos + 2] << 8 | area[pos + 3]; header = 4;
                }
                if (area[pos] == 0x03) { nlen = len; r = pos + header; break; }
                pos += header + len;
            }
            if (r == 0 && (area.Length == 0 || area[0] != 0x03)) return false;
            if (nlen == 0) { format = DataFormat.Text; value = ""; return true; }
            if (r + nlen > area.Length || nlen < 5) return false;
            byte flags = area[r]; int typeLength = area[r + 1]; bool shortRecord = (flags & 0x10) != 0; bool idPresent = (flags & 0x08) != 0;
            int cursor = r + 2; int plen;
            if (shortRecord) { if (cursor >= area.Length) return false; plen = area[cursor++]; }
            else
            {
                if (cursor + 3 >= area.Length) return false;
                plen = area[cursor] << 24 | area[cursor + 1] << 16 | area[cursor + 2] << 8 | area[cursor + 3]; cursor += 4;
            }
            int idLength = 0; if (idPresent) { if (cursor >= area.Length) return false; idLength = area[cursor++]; }
            if (typeLength != 1 || cursor + typeLength + idLength + plen > r + nlen) return false;
            byte type = area[cursor]; int payloadStart = cursor + typeLength + idLength;
            if (type == (byte)'T' && plen >= 1)
            {
                int lang = area[payloadStart] & 0x3F; int start = payloadStart + 1 + lang; int count = plen - 1 - lang; if (count < 0) return false; format = DataFormat.Text; value = Encoding.UTF8.GetString(area, start, count); return true;
            }
            if (type == (byte)'U' && plen >= 1)
            {
                string[] prefixes = { "", "http://www.", "https://www.", "http://", "https://" }; int code = area[payloadStart]; string prefix = code < prefixes.Length ? prefixes[code] : ""; format = DataFormat.Url; value = prefix + Encoding.UTF8.GetString(area, payloadStart + 1, plen - 1); return true;
            }
            return false;
        }
    }

    internal static class AppEnvelope
    {
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("NFS1");
        public static byte[] Encode(DataFormat format, string text)
        {
            byte[] payload = Encoding.UTF8.GetBytes(text); byte[] result = new byte[11 + payload.Length]; Array.Copy(Magic, result, 4); result[4] = (byte)format; result[5] = (byte)(payload.Length & 255); result[6] = (byte)(payload.Length >> 8); uint crc = Crc32.Compute(payload); Array.Copy(BitConverter.GetBytes(crc), 0, result, 7, 4); Array.Copy(payload, 0, result, 11, payload.Length); return result;
        }
        public static bool TryDecode(byte[] area, out DataFormat format, out string value)
        {
            format = DataFormat.Hex; value = ""; if (area.Length < 11) return false; for (int i = 0; i < 4; i++) if (area[i] != Magic[i]) return false;
            int len = area[5] | area[6] << 8; if (len < 0 || 11 + len > area.Length) return false; byte[] payload = new byte[len]; Array.Copy(area, 11, payload, 0, len); uint stored = BitConverter.ToUInt32(area, 7); if (stored != Crc32.Compute(payload)) return false;
            format = area[4] == 1 ? DataFormat.Url : DataFormat.Text; value = Encoding.UTF8.GetString(payload); return true;
        }
    }

    internal static class BackupFile
    {
        private const string Magic = "NFCSTUDIO_BACKUP_V1";
        public static string SuggestedName(CardInfo info) { return DateTime.Now.ToString("yyyyMMdd-HHmmss") + "_" + info.UidText.Replace(":", "") + ".nfcbak"; }
        public static string AutoPath(CardInfo info)
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NfcStudio", "Backups"); Directory.CreateDirectory(dir); return Path.Combine(dir, SuggestedName(info));
        }
        public static void Save(string path, CardDump dump)
        {
            using (BinaryWriter w = new BinaryWriter(File.Create(path), Encoding.UTF8))
            {
                w.Write(Magic); w.Write((int)dump.Info.Kind); w.Write(dump.Info.DisplayType); w.Write(dump.Info.Uid.Length); w.Write(dump.Info.Uid); w.Write(dump.Info.Atr.Length); w.Write(dump.Info.Atr); w.Write(dump.Units.Count);
                foreach (KeyValuePair<int, byte[]> kv in dump.Units) { w.Write(kv.Key); w.Write(kv.Value.Length); w.Write(kv.Value); }
            }
        }
        public static CardDump Load(string path)
        {
            using (BinaryReader r = new BinaryReader(File.OpenRead(path), Encoding.UTF8))
            {
                if (r.ReadString() != Magic) throw new InvalidDataException("Format de sauvegarde inconnu.");
                CardInfo info = new CardInfo { Kind = (CardKind)r.ReadInt32(), DisplayType = r.ReadString() }; info.Uid = r.ReadBytes(r.ReadInt32()); info.Atr = r.ReadBytes(r.ReadInt32()); NtagProfile.Configure(info); CardDump d = new CardDump { Info = info }; int count = r.ReadInt32();
                if (count < 0 || count > 1000) throw new InvalidDataException("Sauvegarde endommagée."); for (int i = 0; i < count; i++) { int address = r.ReadInt32(), len = r.ReadInt32(); if (len < 0 || len > 1024) throw new InvalidDataException("Sauvegarde endommagée."); d.Units[address] = r.ReadBytes(len); } return d;
            }
        }
    }

    internal static class Hex
    {
        public static string Join(byte[] data, string sep) { if (data == null) return ""; string[] parts = new string[data.Length]; for (int i = 0; i < data.Length; i++) parts[i] = data[i].ToString("X2"); return string.Join(sep, parts); }
        public static bool TryParse(string text, out byte[] result)
        {
            string clean = text.Replace(" ", "").Replace("\r", "").Replace("\n", "").Replace("-", "").Replace(":", "").Replace("\t", "");
            result = new byte[0]; if (clean.Length % 2 != 0) return false; byte[] b = new byte[clean.Length / 2]; for (int i = 0; i < b.Length; i++) if (!byte.TryParse(clean.Substring(i * 2, 2), System.Globalization.NumberStyles.HexNumber, null, out b[i])) return false; result = b; return true;
        }
        public static string Ascii(byte[] data) { StringBuilder s = new StringBuilder(); foreach (byte b in data) s.Append(b >= 32 && b < 127 ? (char)b : '.'); return s.ToString(); }
        public static byte[] TrimTrailingZeros(byte[] data) { int n = data.Length; while (n > 0 && data[n - 1] == 0) n--; byte[] r = new byte[n]; Array.Copy(data, r, n); return r; }
        public static byte[] Concat(byte[] a, byte[] b) { byte[] r = new byte[a.Length + b.Length]; Array.Copy(a, r, a.Length); Array.Copy(b, 0, r, a.Length, b.Length); return r; }
    }

    internal static class Crc32
    {
        public static uint Compute(byte[] data) { uint crc = 0xFFFFFFFF; foreach (byte b in data) { crc ^= b; for (int i = 0; i < 8; i++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1; } return ~crc; }
    }

    internal static class SelfTest
    {
        public static string Run()
        {
            byte[] text = Ndef.Encode(DataFormat.Text, "Bonjour éà € 123 !"); DataFormat f; string v; if (!Ndef.TryDecode(text, out f, out v) || v != "Bonjour éà € 123 !" || f != DataFormat.Text) return "ECHEC NDEF texte";
            byte[] url = Ndef.Encode(DataFormat.Url, "https://www.example.com/a?b=1&c=é"); if (!Ndef.TryDecode(url, out f, out v) || v != "https://www.example.com/a?b=1&c=é" || f != DataFormat.Url) return "ECHEC NDEF URL";
            string longText = new string('é', 300); byte[] longNdef = Ndef.Encode(DataFormat.Text, longText); if (longNdef.Length <= 255 || !Ndef.TryDecode(longNdef, out f, out v) || v != longText || f != DataFormat.Text) return "ECHEC NDEF étendu NTAG215/216";
            CardInfo p213 = new CardInfo { Kind = CardKind.Ntag213 }; NtagProfile.Configure(p213); if (p213.UserBytes != 144 || p213.UserLastPage != 39) return "ECHEC profil NTAG213";
            CardInfo p215 = new CardInfo { Kind = CardKind.Ntag215 }; NtagProfile.Configure(p215); if (p215.UserBytes != 504 || p215.UserLastPage != 129) return "ECHEC profil NTAG215";
            CardInfo p216 = new CardInfo { Kind = CardKind.Ntag216 }; NtagProfile.Configure(p216); if (p216.UserBytes != 888 || p216.UserLastPage != 225) return "ECHEC profil NTAG216";
            byte[] env = AppEnvelope.Encode(DataFormat.Text, "Test spécial €"); if (!AppEnvelope.TryDecode(env, out f, out v) || v != "Test spécial €") return "ECHEC enveloppe Classic";
            byte[] hex; if (!Hex.TryParse("00 FF A1", out hex) || hex.Length != 3 || Hex.TryParse("ABC", out hex)) return "ECHEC validation hex";
            try { using (Pcsc p = new Pcsc()) return "OK — codecs, validation et PC/SC. Lecteurs : " + string.Join(", ", p.ListReaders().ToArray()); }
            catch (Exception ex) { return "OK codecs; PC/SC indisponible : " + ex.Message; }
        }

        public static string ReaderTest()
        {
            try
            {
                using (Pcsc p = new Pcsc())
                {
                    List<string> readers = p.ListReaders();
                    p.ReaderName = readers.Find(delegate(string s) { return s.IndexOf("ACR122", StringComparison.OrdinalIgnoreCase) >= 0; });
                    if (p.ReaderName == null) return "ECHEC — ACR122 introuvable";
                    CardInfo card = p.Detect();
                    if (card == null) return "OK lecteur — aucune puce posée";
                    CardDump dump = p.ReadDump(card, new byte[] { 255, 255, 255, 255, 255, 255 }, true);
                    return string.Format("OK lecture — {0}, UID {1}, {2} unités lues, {3} secteurs inaccessibles", card.DisplayType, card.UidText, dump.Units.Count, dump.Inaccessible.Count);
                }
            }
            catch (Exception ex) { return "ECHEC lecteur — " + ex.Message; }
        }
    }
}


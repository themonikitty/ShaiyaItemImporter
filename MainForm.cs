using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace ShaiyaItemImporter
{
    public partial class MainForm : Form
    {
        private readonly ImporterEngine _engine = new();
        private List<ItemEntry> _currentFiltered = new();

        private TextBox _txtDonorPath = null!;
        private TextBox _txtTargetPath = null!;
        private TextBox _txtSqlConn = null!;
        private Button _btnLoad = null!;
        private CheckBox _chkOnlyNew = null!;
        private CheckBox _chkAllowHigherTypes = null!;
        private ComboBox _cboCategory = null!;
        private TextBox _txtSearch = null!;
        private Label _lblCounts = null!;
        private DataGridView _grid = null!;
        private CheckBox _chkCopyAssets = null!;
        private CheckBox _chkCopyIcons = null!;
        private CheckBox _chkUpdateSData = null!;
        private CheckBox _chkUpdateSql = null!;
        private CheckBox _chkSqlBackup = null!;
        private Button _btnImport = null!;
        private ProgressBar _progressBar = null!;
        private TextBox _txtLog = null!;

        public MainForm()
        {
            InitializeComponent();
            _engine.OnLog += AppendLog;
            _engine.OnProgress += UpdateProgress;
            this.FormClosing += (s, e) => _engine.Dispose();
        }

        private void InitializeComponent()
        {
            this.Text = "Shaiya Auto Item Importer - Shaiya Core Stable v1.4";
            this.Size = new Size(1280, 820);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

            var mainLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Padding = new Padding(10)
            };
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 115F));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 55F));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 195F));
            this.Controls.Add(mainLayout);

            // 1. Top Panel
            var topGroup = new GroupBox
            {
                Text = "Configurações de Origem (Doador), Destino e Banco de Dados",
                Dock = DockStyle.Fill,
                Padding = new Padding(8)
            };
            var topGrid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 4,
                RowCount = 3
            };
            topGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180F));
            topGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            topGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90F));
            topGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180F));

            topGrid.Controls.Add(new Label { Text = "Cliente Doador (data.sah):", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
            _txtDonorPath = new TextBox
            {
                Text = @"C:\Users\Gustavo.Machado\Downloads\Shaiya Essentials v16\Client\data.sah",
                Dock = DockStyle.Fill
            };
            topGrid.Controls.Add(_txtDonorPath, 1, 0);
            var btnBrowseDonor = new Button { Text = "Procurar...", Dock = DockStyle.Fill };
            btnBrowseDonor.Click += (s, e) => BrowseSah(_txtDonorPath);
            topGrid.Controls.Add(btnBrowseDonor, 2, 0);

            _btnLoad = new Button
            {
                Text = "🔄 Carregar e Analisar",
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                BackColor = Color.FromArgb(0, 122, 204),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Dock = DockStyle.Fill
            };
            topGrid.SetRowSpan(_btnLoad, 3);
            _btnLoad.Click += async (s, e) => await LoadDataAsync();
            topGrid.Controls.Add(_btnLoad, 3, 0);

            topGrid.Controls.Add(new Label { Text = "Seu Cliente Core (data.sah):", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
            _txtTargetPath = new TextBox
            {
                Text = @"C:\Users\Gustavo.Machado\Desktop\LW PROJETOS\#SERVIDORES\Shaiya Core Stable v1.4\Shaiya Core Client\data.sah",
                Dock = DockStyle.Fill
            };
            topGrid.Controls.Add(_txtTargetPath, 1, 1);
            var btnBrowseTarget = new Button { Text = "Procurar...", Dock = DockStyle.Fill };
            btnBrowseTarget.Click += (s, e) => BrowseSah(_txtTargetPath);
            topGrid.Controls.Add(btnBrowseTarget, 2, 1);

            topGrid.Controls.Add(new Label { Text = "SQL Connection String:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
            _txtSqlConn = new TextBox
            {
                Text = "Server=127.0.0.1;Database=PS_GameDefs;User Id=shaiya;Password=shaiya123;TrustServerCertificate=True;",
                Dock = DockStyle.Fill
            };
            topGrid.Controls.Add(_txtSqlConn, 1, 2);
            var btnTestSql = new Button { Text = "Testar SQL", Dock = DockStyle.Fill };
            btnTestSql.Click += (s, e) => TestSqlConnection();
            topGrid.Controls.Add(btnTestSql, 2, 2);

            topGroup.Controls.Add(topGrid);
            mainLayout.Controls.Add(topGroup, 0, 0);

            // 2. Filter Bar
            var filterPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = new Padding(0, 5, 0, 5)
            };

            _chkOnlyNew = new CheckBox
            {
                Text = "✨ Apenas novos / não duplicados",
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                ForeColor = Color.DarkGreen,
                AutoSize = true,
                Checked = true,
                Margin = new Padding(0, 5, 12, 0)
            };
            _chkOnlyNew.CheckedChanged += (s, e) => { _engine.SelectNewAndNonDuplicate(_chkOnlyNew.Checked); ApplyFilters(); };
            filterPanel.Controls.Add(_chkOnlyNew);

            _chkAllowHigherTypes = new CheckBox
            {
                Text = "Liberar Type > 100",
                ForeColor = Color.DarkRed,
                AutoSize = true,
                Checked = false,
                Margin = new Padding(0, 5, 12, 0)
            };
            filterPanel.Controls.Add(_chkAllowHigherTypes);

            filterPanel.Controls.Add(new Label { Text = "Categoria:", AutoSize = true, Margin = new Padding(0, 7, 4, 0) });
            _cboCategory = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 165 };
            _cboCategory.Items.Add("Todas as Categorias");
            _cboCategory.SelectedIndex = 0;
            _cboCategory.SelectedIndexChanged += (s, e) => ApplyFilters();
            filterPanel.Controls.Add(_cboCategory);

            filterPanel.Controls.Add(new Label { Text = "Buscar:", AutoSize = true, Margin = new Padding(10, 7, 4, 0) });
            _txtSearch = new TextBox { Width = 140 };
            _txtSearch.TextChanged += (s, e) => ApplyFilters();
            filterPanel.Controls.Add(_txtSearch);

            var btnSelectAll = new Button { Text = "Marcar Visíveis", AutoSize = true, Margin = new Padding(10, 2, 4, 0) };
            btnSelectAll.Click += (s, e) => SetSelectionForVisible(true);
            filterPanel.Controls.Add(btnSelectAll);

            var btnDeselectAll = new Button { Text = "Desmarcar", AutoSize = true, Margin = new Padding(0, 2, 10, 0) };
            btnDeselectAll.Click += (s, e) => SetSelectionForVisible(false);
            filterPanel.Controls.Add(btnDeselectAll);

            _lblCounts = new Label
            {
                Text = "Aguardando carregamento...",
                Font = new Font("Segoe UI", 8.5F, FontStyle.Italic),
                AutoSize = true,
                Margin = new Padding(0, 7, 0, 0)
            };
            filterPanel.Controls.Add(_lblCounts);

            mainLayout.Controls.Add(filterPanel, 0, 1);

            // 3. DataGrid
            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AutoGenerateColumns = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                RowHeadersVisible = false,
                BackgroundColor = Color.White
            };

            _grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Importar", DataPropertyName = "IsSelected", Width = 65 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Status", DataPropertyName = "StatusDisplay", Width = 135, ReadOnly = true });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Categoria", DataPropertyName = "TypeName", Width = 130, ReadOnly = true });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Type", DataPropertyName = "Type", Width = 50, ReadOnly = true });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "ID", DataPropertyName = "TypeId", Width = 50, ReadOnly = true });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Nome no Doador", DataPropertyName = "DonorName", Width = 190, ReadOnly = true });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Nome Atual no Core", DataPropertyName = "CoreName", Width = 170, ReadOnly = true });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Nível", DataPropertyName = "ReqLevel", Width = 55, ReadOnly = true });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Modelo", DataPropertyName = "Model", Width = 60, ReadOnly = true });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Ícone", DataPropertyName = "Icon", Width = 55, ReadOnly = true });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Atributos / Status", DataPropertyName = "StatsSummary", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = true });

            _grid.CellFormatting += Grid_CellFormatting;
            _grid.CellValueChanged += (s, e) => { if (e.ColumnIndex == 0) UpdateCountsLabel(); };
            _grid.CurrentCellDirtyStateChanged += (s, e) => { if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };

            mainLayout.Controls.Add(_grid, 0, 2);

            // 4. Bottom Action & Log Panel
            var bottomLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1
            };
            bottomLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 460F));
            bottomLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

            var actionGroup = new GroupBox { Text = "Opções de Execução da Importação", Dock = DockStyle.Fill, Padding = new Padding(8) };
            var actionFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown };

            _chkCopyAssets = new CheckBox { Text = "Copiar modelos 3D (.3DO/.3DC) e texturas (.DDS) de armas/roupas", Checked = true, AutoSize = true, Margin = new Padding(0, 1, 0, 1) };
            _chkCopyIcons = new CheckBox { Text = "Copiar folhas de ícones (Interface/ItemIcon*.dds)", Checked = true, AutoSize = true, Margin = new Padding(0, 1, 0, 1) };
            _chkUpdateSData = new CheckBox { Text = "Atualizar catálogo do cliente e servidor (Item.SData)", Checked = true, AutoSize = true, Margin = new Padding(0, 1, 0, 1) };
            _chkUpdateSql = new CheckBox { Text = "Inserir/Atualizar no SQL Server (PS_GameDefs)", Checked = true, AutoSize = true, Margin = new Padding(0, 1, 0, 1) };
            _chkSqlBackup = new CheckBox { Text = "Gerar script .SQL de backup das alterações", Checked = true, AutoSize = true, Margin = new Padding(0, 1, 0, 4) };

            actionFlow.Controls.Add(_chkCopyAssets);
            actionFlow.Controls.Add(_chkCopyIcons);
            actionFlow.Controls.Add(_chkUpdateSData);
            actionFlow.Controls.Add(_chkUpdateSql);
            actionFlow.Controls.Add(_chkSqlBackup);

            _btnImport = new Button
            {
                Text = "🚀 INICIAR IMPORTAÇÃO AUTOMATIZADA",
                Font = new Font("Segoe UI", 10.5F, FontStyle.Bold),
                BackColor = Color.FromArgb(46, 139, 87),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Size = new Size(430, 38),
                Cursor = Cursors.Hand
            };
            _btnImport.Click += async (s, e) => await StartImportAsync();
            actionFlow.Controls.Add(_btnImport);

            actionGroup.Controls.Add(actionFlow);
            bottomLayout.Controls.Add(actionGroup, 0, 0);

            var logGroup = new GroupBox { Text = "Progresso e Registro de Atividades", Dock = DockStyle.Fill, Padding = new Padding(8) };
            var logGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            logGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 24F));
            logGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            _progressBar = new ProgressBar { Dock = DockStyle.Fill, Minimum = 0, Maximum = 100, Value = 0 };
            logGrid.Controls.Add(_progressBar, 0, 0);

            _txtLog = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.LightGreen,
                Font = new Font("Consolas", 8.5F)
            };
            logGrid.Controls.Add(_txtLog, 0, 1);

            logGroup.Controls.Add(logGrid);
            bottomLayout.Controls.Add(logGroup, 1, 0);

            mainLayout.Controls.Add(bottomLayout, 0, 3);
        }

        private void BrowseSah(TextBox target)
        {
            using var ofd = new OpenFileDialog
            {
                Filter = "Shaiya Data File (*.sah)|*.sah|Todos os Arquivos (*.*)|*.*",
                Title = "Selecione o arquivo data.sah"
            };
            if (ofd.ShowDialog() == DialogResult.OK)
            {
                target.Text = ofd.FileName;
            }
        }

        private void TestSqlConnection()
        {
            try
            {
                using var conn = new Microsoft.Data.SqlClient.SqlConnection(_txtSqlConn.Text);
                conn.Open();
                MessageBox.Show("Conexão com o SQL Server (PS_GameDefs) estabelecida com sucesso!", "Sucesso", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Falha ao conectar no SQL Server: " + ex.Message, "Erro de Conexão", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async System.Threading.Tasks.Task LoadDataAsync()
        {
            if (!File.Exists(_txtDonorPath.Text))
            {
                MessageBox.Show("Arquivo doador não encontrado: " + _txtDonorPath.Text, "Aviso", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!File.Exists(_txtTargetPath.Text))
            {
                MessageBox.Show("Arquivo do seu cliente não encontrado: " + _txtTargetPath.Text, "Aviso", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _btnLoad.Enabled = false;
            _btnImport.Enabled = false;
            AppendLog("Iniciando carregamento e detecção de episódios...");

            try
            {
                await _engine.LoadClientsAsync(_txtDonorPath.Text, _txtTargetPath.Text, _chkAllowHigherTypes.Checked);

                _cboCategory.Items.Clear();
                _cboCategory.Items.Add("Todas as Categorias");
                var categories = _engine.LoadedEntries
                    .Select(i => i.TypeName)
                    .Distinct()
                    .OrderBy(n => n)
                    .ToArray();
                _cboCategory.Items.AddRange(categories);
                _cboCategory.SelectedIndex = 0;

                _engine.SelectNewAndNonDuplicate(_chkOnlyNew.Checked);
                ApplyFilters();
                MessageBox.Show(
                    $"Carregamento concluído!\n\n" +
                    $"• Doador: {_engine.DonorEpisode} | Core Destino: {_engine.TargetEpisode}\n" +
                    $"• Limite de Type na Core: até {_engine.MaxSupportedTargetType}\n" +
                    $"• Total analisado: {_engine.LoadedEntries.Count} itens",
                    "Sucesso",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                AppendLog("ERRO: " + ex.Message);
                MessageBox.Show("Erro ao carregar arquivos: " + ex.Message, "Erro", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _btnLoad.Enabled = true;
                _btnImport.Enabled = true;
            }
        }

        private void ApplyFilters()
        {
            if (_engine.LoadedEntries.Count == 0) return;

            bool onlyNew = _chkOnlyNew.Checked;
            string selectedCat = _cboCategory.SelectedItem?.ToString() ?? "Todas as Categorias";
            string query = _txtSearch.Text.Trim();

            _currentFiltered = _engine.LoadedEntries.Where(item =>
            {
                if (onlyNew && item.Status != ItemImportStatus.NewItem && item.Status != ItemImportStatus.ReplacePlaceholder)
                    return false;
                if (selectedCat != "Todas as Categorias" && item.TypeName != selectedCat)
                    return false;
                if (!string.IsNullOrEmpty(query))
                {
                    bool matchName = item.DonorName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
                    bool matchId = item.ItemId.ToString().Contains(query) || item.TypeId.ToString().Contains(query);
                    if (!matchName && !matchId) return false;
                }
                return true;
            }).ToList();

            

            _grid.DataSource = null;
            _grid.DataSource = _currentFiltered;
            UpdateCountsLabel();
        }

        private void SetSelectionForVisible(bool select)
        {
            foreach (var item in _currentFiltered)
            {
                if (select && (item.Status == ItemImportStatus.IncompatibleType || item.Status == ItemImportStatus.EmptyOrInvalid))
                    continue;
                item.IsSelected = select;
            }
            _grid.Refresh();
            UpdateCountsLabel();
        }

        private void UpdateCountsLabel()
        {
            int total = _engine.LoadedEntries.Count;
            int visible = _currentFiltered.Count;
            int selected = _engine.LoadedEntries.Count(i => i.IsSelected);
            int newAvailable = _engine.LoadedEntries.Count(i =>
                i.Status == ItemImportStatus.NewItem || i.Status == ItemImportStatus.ReplacePlaceholder);

            _lblCounts.Text = $"Total: {total} | Novos: {newAvailable} | Exibindo: {visible} | Selecionados: {selected}";
        }

        private void Grid_CellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _currentFiltered.Count) return;
            var item = _currentFiltered[e.RowIndex];

            if (_grid.Columns[e.ColumnIndex].DataPropertyName == "StatusDisplay")
            {
                if (item.Status == ItemImportStatus.NewItem)
                {
                    e.CellStyle!.ForeColor = Color.DarkGreen;
                    e.CellStyle.Font = new Font(_grid.Font, FontStyle.Bold);
                }
                else if (item.Status == ItemImportStatus.ReplacePlaceholder)
                {
                    e.CellStyle!.ForeColor = Color.DarkBlue;
                    e.CellStyle.Font = new Font(_grid.Font, FontStyle.Bold);
                }
                else if (item.Status == ItemImportStatus.IncompatibleType || item.Status == ItemImportStatus.EmptyOrInvalid)
                {
                    e.CellStyle!.ForeColor = Color.DarkRed;
                    e.CellStyle.Font = new Font(_grid.Font, FontStyle.Bold);
                }
                else
                {
                    e.CellStyle!.ForeColor = Color.Gray;
                }
            }
        }

        private async System.Threading.Tasks.Task StartImportAsync()
        {
            var selected = _engine.LoadedEntries.Where(i => i.IsSelected).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("Nenhum item foi selecionado para importação.", "Aviso", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var confirm = MessageBox.Show(
                $"Deseja importar {selected.Count} itens selecionados para o seu Shaiya Core?\n\n" +
                $"• Modelos 3D e texturas DDS: {(_chkCopyAssets.Checked ? "Sim" : "Não")}\n" +
                $"• Folhas de ícones (ItemIcon): {(_chkCopyIcons.Checked ? "Sim" : "Não")}\n" +
                $"• Atualizar Item.SData ({_engine.TargetEpisode}): {(_chkUpdateSData.Checked ? "Sim" : "Não")}\n" +
                $"• Gravar no SQL Server: {(_chkUpdateSql.Checked ? "Sim" : "Não")}\n\n" +
                "Um backup automático do data.sah será criado antes da gravação.",
                "Confirmar Importação Automatizada",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (confirm != DialogResult.Yes) return;

            _btnLoad.Enabled = false;
            _btnImport.Enabled = false;
            _progressBar.Value = 0;

            string backupSqlPath = Path.Combine(
                Path.GetDirectoryName(_txtTargetPath.Text) ?? "",
                $"imported_items_{DateTime.Now:yyyyMMdd_HHmmss}.sql");

            try
            {
                int imported = await _engine.ExecuteImportAsync(
                    selected,
                    _txtTargetPath.Text,
                    _chkCopyAssets.Checked,
                    _chkCopyIcons.Checked,
                    _chkUpdateSData.Checked,
                    _chkUpdateSql.Checked,
                    _txtSqlConn.Text,
                    _chkSqlBackup.Checked,
                    backupSqlPath);

                MessageBox.Show(
                    $"Importação concluída com sucesso!\n\n" +
                    $"• Itens processados: {imported}\n" +
                    $"• Backup SQL gerado em: {backupSqlPath}\n\n" +
                    "Reinicie o serviço ps_game para carregar os novos itens!",
                    "Importação Concluída",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                AppendLog("ERRO NA IMPORTAÇÃO: " + ex.Message);
                MessageBox.Show("Erro durante a importação: " + ex.Message, "Erro", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _btnLoad.Enabled = true;
                _btnImport.Enabled = true;
                _progressBar.Value = 100;
            }
        }

        private void AppendLog(string text)
        {
            if (this.InvokeRequired)
            {
                this.Invoke(new Action(() => AppendLog(text)));
                return;
            }
            _txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}\r\n");
        }

        private void UpdateProgress(int current, int total)
        {
            if (this.InvokeRequired)
            {
                this.Invoke(new Action(() => UpdateProgress(current, total)));
                return;
            }
            if (total > 0)
            {
                _progressBar.Value = Math.Min(100, (int)((current / (double)total) * 100));
            }
        }
    }
}

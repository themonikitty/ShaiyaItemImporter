using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Parsec;
using Parsec.Common;
using Parsec.Shaiya.Data;
using Parsec.Shaiya.Item;
using Parsec.Shaiya.SData;

namespace ShaiyaItemImporter
{
    public class ImporterEngine : IDisposable
    {
        public Data? DonorData { get; private set; }
        public Data? TargetData { get; private set; }
        public Item? DonorItem { get; private set; }
        public Item? TargetItem { get; private set; }

        public Episode DonorEpisode { get; private set; } = Episode.EP6;
        public Episode TargetEpisode { get; private set; } = Episode.EP6;
        public bool TargetWasEncrypted { get; private set; }
        public byte MaxSupportedTargetType { get; private set; } = 100;

        public List<ItemEntry> LoadedEntries { get; private set; } = new();

        private static readonly HashSet<byte> CashItemTypes = new() { 90, 91, 92, 93, 95, 100, 101, 102, 120, 121, 150 };

        private static readonly HashSet<byte> ArmorAndCostumeTypes = new()
        {
            16, 17, 18, 20, 21, 22, 23, 24, 26, 27,
            36, 37, 38, 40, 41,
            51, 52, 53, 55, 56,
            67, 68, 69, 70, 71, 72, 73, 74, 75, 76, 77,
            90, 91, 92, 93, 150
        };

        private static readonly HashSet<byte> WeaponAndShieldTypes = new()
        {
            1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 19, 25,
            45, 46, 47, 48, 49, 50, 57, 58, 59, 60, 61, 62, 63, 64, 65, 66
        };

        private static readonly Regex EmbeddedTextureRegex = new(
            @"([a-zA-Z0-9_\-\.]+\.(?:dds|tga))",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ValidLetterRegex = new(
            @"[A-Za-zÀ-ÿ]{2,}",
            RegexOptions.Compiled);

        public event Action<string>? OnLog;
        public event Action<int, int>? OnProgress;

        private void Log(string msg) => OnLog?.Invoke(msg);

        private static string NormalizeRelPath(string path)
        {
            string norm = path.Replace('\\', '/').TrimStart('/');
            if (norm.StartsWith("data/", StringComparison.OrdinalIgnoreCase))
                norm = norm.Substring(5);
            return norm;
        }

        public async Task LoadClientsAsync(string donorSahPath, string targetSahPath, bool allowHigherTypes = false)
        {
            await Task.Run(() =>
            {
                DisposeData();

                Log("Carregando arquivos do cliente doador...");
                string donorSafPath = Path.ChangeExtension(donorSahPath, ".saf");
                DonorData = new Data(donorSahPath, donorSafPath);
                (DonorItem, DonorEpisode, bool donorEnc) = ReadItemSDataAuto(DonorData, "Doador");
                Log($"Doador carregado: {DonorData.FileIndex.Count} arquivos | Formato: {DonorEpisode} | Criptografado: {(donorEnc ? "Sim" : "Não")}");

                Log("Carregando arquivos do seu cliente (destino)...");
                string targetSafPath = Path.ChangeExtension(targetSahPath, ".saf");
                TargetData = new Data(targetSahPath, targetSafPath);
                (TargetItem, TargetEpisode, TargetWasEncrypted) = ReadItemSDataAuto(TargetData, "Core Destino");

                int coreGroupCap = Math.Max(100, TargetItem.ItemGroups.Count - 1);
                MaxSupportedTargetType = allowHigherTypes
                    ? (byte)Math.Max(150, coreGroupCap)
                    : (byte)100;

                Log($"Destino carregado: {TargetData.FileIndex.Count} arquivos | Formato: {TargetEpisode} | Grupos SData Core: {TargetItem.ItemGroups.Count} | MaxType liberado: {MaxSupportedTargetType}");

                Log("Comparando e deduplicando itens entre os dois clientes...");
                BuildEntries();
                Log($"Total de {LoadedEntries.Count} itens únicos analisados.");
            });
        }

        private (Item item, Episode detectedEpisode, bool wasEncrypted) ReadItemSDataAuto(Data data, string label)
        {
            var sfile = data.GetFile("Item/Item.SData")
                ?? throw new FileNotFoundException($"Arquivo Item/Item.SData não encontrado no cliente ({label}).");

            byte[] raw = data.GetFileBuffer(sfile);
            bool isEncrypted = SData.IsEncrypted(raw);
            byte[] dec = isEncrypted ? SData.Decrypt(raw) : raw;

            Episode[] candidates = { Episode.EP6, Episode.EP6_4, Episode.EP5, Episode.EP8 };
            Item? bestItem = null;
            Episode bestEp = Episode.EP6;
            int bestScore = -1;

            foreach (var ep in candidates)
            {
                try
                {
                    var parsed = ParsecReader.FromBuffer<Item>("Item.SData", dec, ep);
                    if (parsed == null || parsed.ItemGroups == null || parsed.ItemGroups.Count == 0)
                        continue;

                    int validDefs = 0;
                    int invalidDefs = 0;

                    for (int g = 0; g < parsed.ItemGroups.Count; g++)
                    {
                        foreach (var def in parsed.ItemGroups[g].ItemDefinitions)
                        {
                            if (def.ItemType == g && !string.IsNullOrEmpty(def.Name) && !def.Name.Contains('\0'))
                                validDefs++;
                            else if (def.ItemType != g)
                                invalidDefs++;
                        }
                    }

                    byte[] reserialized = parsed.GetBytes(ep).ToArray();
                    int sizeBonus = (reserialized.Length == dec.Length) ? 100000 : 0;
                    int score = sizeBonus + (validDefs * 10) - (invalidDefs * 50);

                    if (score > bestScore && validDefs > 0)
                    {
                        bestScore = score;
                        bestItem = parsed;
                        bestEp = ep;
                    }
                }
                catch
                {
                    // Tentar próximo Episode
                }
            }

            if (bestItem == null)
            {
                bestEp = Episode.EP6_4;
                bestItem = ParsecReader.FromBuffer<Item>("Item.SData", dec, bestEp);
            }

            return (bestItem, bestEp, isEncrypted);
        }

        private void BuildEntries()
        {
            LoadedEntries.Clear();
            if (DonorItem == null || TargetItem == null) return;

            var targetDict = new Dictionary<string, ItemDefinition>(StringComparer.Ordinal);
            foreach (var grp in TargetItem.ItemGroups)
            {
                foreach (var def in grp.ItemDefinitions)
                {
                    if (def.ItemType == 0 || def.ItemTypeId == 0) continue;
                    string key = $"{def.ItemType}_{def.ItemTypeId}";
                    targetDict.TryAdd(key, def);
                }
            }

            var uniqueDonorDefs = new Dictionary<string, ItemDefinition>(StringComparer.Ordinal);
            int skippedDuplicates = 0;

            foreach (var grp in DonorItem.ItemGroups)
            {
                foreach (var donorDef in grp.ItemDefinitions)
                {
                    if (donorDef.ItemType == 0 || donorDef.ItemTypeId == 0)
                        continue;

                    string key = $"{donorDef.ItemType}_{donorDef.ItemTypeId}";
                    if (!uniqueDonorDefs.TryGetValue(key, out var existing))
                    {
                        uniqueDonorDefs[key] = donorDef;
                    }
                    else
                    {
                        skippedDuplicates++;
                        if (IsDonorItemEmptyOrDummy(existing) && !IsDonorItemEmptyOrDummy(donorDef))
                        {
                            uniqueDonorDefs[key] = donorDef;
                        }
                    }
                }
            }

            if (skippedDuplicates > 0)
            {
                Log($"Aviso: {skippedDuplicates} registros duplicados no Item.SData doador foram filtrados.");
            }

            foreach (var kv in uniqueDonorDefs.OrderBy(k => k.Value.ItemType).ThenBy(k => k.Value.ItemTypeId))
            {
                var donorDef = kv.Value;
                targetDict.TryGetValue(kv.Key, out var coreDef);

                bool isDonorEmpty = IsDonorItemEmptyOrDummy(donorDef);
                bool isCorePlaceholder = coreDef == null || string.IsNullOrWhiteSpace(coreDef.Name) || coreDef.Name.StartsWith("?") || coreDef.Name.Contains("??");

                ItemImportStatus status;
                if (isDonorEmpty)
                {
                    status = ItemImportStatus.EmptyOrInvalid;
                }
                else if (donorDef.ItemType > MaxSupportedTargetType)
                {
                    status = ItemImportStatus.IncompatibleType;
                }
                else if (coreDef == null)
                {
                    status = ItemImportStatus.NewItem;
                }
                else if (isCorePlaceholder)
                {
                    status = ItemImportStatus.ReplacePlaceholder;
                }
                else
                {
                    status = ItemImportStatus.AlreadyExists;
                }

                LoadedEntries.Add(new ItemEntry
                {
                    IsSelected = false,
                    Status = status,
                    Type = donorDef.ItemType,
                    TypeName = GetCategoryName(donorDef.ItemType),
                    TypeId = donorDef.ItemTypeId,
                    DonorName = donorDef.Name ?? "",
                    CoreName = coreDef?.Name ?? "(Não existe)",
                    ReqLevel = donorDef.MinLevel,
                    Model = donorDef.Model,
                    Icon = donorDef.Icon,
                    StatsSummary = BuildStatsSummary(donorDef),
                    DonorDef = donorDef,
                    CoreDef = coreDef
                });
            }
        }

        private static bool IsDonorItemEmptyOrDummy(ItemDefinition d)
        {
            string name = (d.Name ?? "").Replace("\0", "").Trim();

            if (string.IsNullOrWhiteSpace(name) || name.Length < 2)
                return true;

            if (name.Contains("??") || name.StartsWith("?"))
                return true;

            if (!ValidLetterRegex.IsMatch(name))
                return true;

            if (name.Equals("none", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("null", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("empty", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("unused", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("dummy", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("test", StringComparison.OrdinalIgnoreCase))
                return true;

            bool noClassCanUse = d.AttackFighter == 0 && d.DefenseFighter == 0 &&
                                 d.PatrolRogue == 0 && d.ShootRogue == 0 &&
                                 d.AttackMage == 0 && d.DefenseMage == 0;
            if (noClassCanUse)
                return true;

            if (d.Icon == 0 && d.Model == 0)
                return true;

            bool isEquipment = WeaponAndShieldTypes.Contains(d.ItemType) || ArmorAndCostumeTypes.Contains(d.ItemType);
            if (isEquipment)
            {
                bool hasAnyCombatOrStat = d.Attack > 0 || d.AttackAdd > 0 || d.Def > 0 || d.Resist > 0 ||
                                          d.Quality > 0 || d.Slot > 0 || d.Speed > 0 ||
                                          d.Hp > 0 || d.Sp > 0 || d.Mp > 0 ||
                                          d.Str > 0 || d.Dex > 0 || d.Rec > 0 || d.Int > 0 || d.Wis > 0 || d.Luc > 0;
                if (!hasAnyCombatOrStat)
                    return true;
            }

            return false;
        }

        public void SelectNewAndNonDuplicate(bool onlyNew)
        {
            foreach (var item in LoadedEntries)
            {
                if (item.Status == ItemImportStatus.IncompatibleType || item.Status == ItemImportStatus.EmptyOrInvalid)
                {
                    item.IsSelected = false;
                    continue;
                }

                if (onlyNew)
                {
                    item.IsSelected = (item.Status == ItemImportStatus.NewItem || item.Status == ItemImportStatus.ReplacePlaceholder);
                }
                else
                {
                    item.IsSelected = true;
                }
            }
        }

        public async Task<int> ExecuteImportAsync(
            List<ItemEntry> selectedItems,
            string targetSahPath,
            bool copyAssets,
            bool copyIcons,
            bool updateSData,
            bool updateSql,
            string sqlConnectionString,
            bool generateSqlBackup,
            string backupSqlPath,
            bool dryRun = false)
        {
            return await Task.Run(() =>
            {
                if (TargetData == null || TargetItem == null || DonorData == null)
                    throw new InvalidOperationException("Clientes não carregados.");

                var distinctItems = selectedItems
                    .Where(i => i.Type > 0 && i.TypeId > 0)
                    .GroupBy(i => (i.Type, i.TypeId))
                    .Select(g => g.First())
                    .OrderBy(i => i.Type)
                    .ThenBy(i => i.TypeId)
                    .ToList();

                int totalCount = distinctItems.Count;
                Log($"{(dryRun ? "[SIMULAÇÃO DRY-RUN] " : "")}Iniciando importação de {totalCount} itens únicos selecionados...");

                if (dryRun)
                {
                    var previewFiles = CollectRequiredAssetFiles(DonorData, TargetData, distinctItems, copyAssets, copyIcons);
                    int modelsCount = previewFiles.Count(f => f.Name.EndsWith(".3do", StringComparison.OrdinalIgnoreCase) || f.Name.EndsWith(".3dc", StringComparison.OrdinalIgnoreCase));
                    int iconsCount = previewFiles.Count(f => NormalizeRelPath(f.RelativePath).StartsWith("Interface/", StringComparison.OrdinalIgnoreCase));
                    int texCount = previewFiles.Count - modelsCount - iconsCount;
                    Log($"[DRY-RUN] Arquivos visuais selecionados: {previewFiles.Count} total ({modelsCount} modelos 3D, {texCount} texturas DDS/TGA, {iconsCount} folhas de ícones).");

                    var sampleModels = previewFiles
                        .Where(f => f.Name.EndsWith(".3do", StringComparison.OrdinalIgnoreCase) || f.Name.EndsWith(".3dc", StringComparison.OrdinalIgnoreCase))
                        .Select(f => NormalizeRelPath(f.RelativePath))
                        .Take(8);
                    Log("[DRY-RUN] Amostra de modelos 3D: " + string.Join(", ", sampleModels));

                    var sampleTextures = previewFiles
                        .Where(f => !NormalizeRelPath(f.RelativePath).StartsWith("Interface/", StringComparison.OrdinalIgnoreCase) &&
                                    (f.Name.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) || f.Name.EndsWith(".tga", StringComparison.OrdinalIgnoreCase)))
                        .Select(f => NormalizeRelPath(f.RelativePath))
                        .Take(8);
                    Log("[DRY-RUN] Amostra de texturas: " + string.Join(", ", sampleTextures));

                    var sampleIcons = previewFiles
                        .Where(f => NormalizeRelPath(f.RelativePath).StartsWith("Interface/", StringComparison.OrdinalIgnoreCase))
                        .Select(f => NormalizeRelPath(f.RelativePath))
                        .Take(8);
                    Log("[DRY-RUN] Amostra de ícones: " + string.Join(", ", sampleIcons));

                    return totalCount;
                }

                string tempDir = Path.Combine(Path.GetTempPath(), "ShaiyaImport_" + Guid.NewGuid().ToString("N"));
                string patchTempDir = Path.Combine(tempDir, "patch_in");
                string patchOutDir = Path.Combine(tempDir, "patch_out");
                Directory.CreateDirectory(patchTempDir);
                Directory.CreateDirectory(patchOutDir);

                byte[]? finalItemSDataBytes = null;

                try
                {
                    // 1. Atualizar Item.SData
                    if (updateSData)
                    {
                        Log($"Atualizando catálogo do cliente (Item.SData - Formato {TargetEpisode})...");

                        var origSFile = TargetData.GetFile("Item/Item.SData");
                        if (origSFile != null)
                        {
                            string targetDir = Path.GetDirectoryName(targetSahPath) ?? "";
                            byte[] origBytes = TargetData.GetFileBuffer(origSFile);

                            string origBaselineSData = Path.Combine(targetDir, "Item.SData.bak_ORIGINAL");
                            if (!File.Exists(origBaselineSData))
                                File.WriteAllBytes(origBaselineSData, origBytes);

                            string sdataBak = Path.Combine(targetDir, $"Item.SData.bak_{DateTime.Now:yyyyMMdd_HHmmss}");
                            File.WriteAllBytes(sdataBak, origBytes);
                            Log($"Backup do Item.SData salvo em: {Path.GetFileName(sdataBak)}");
                        }

                        foreach (var entry in distinctItems)
                        {
                            byte type = entry.Type;
                            byte typeId = entry.TypeId;

                            while (TargetItem.ItemGroups.Count <= type)
                            {
                                TargetItem.ItemGroups.Add(new ItemGroup());
                            }

                            var group = TargetItem.ItemGroups[type];
                            var existingDefs = group.ItemDefinitions.Where(d => d.ItemTypeId == typeId).ToList();
                            if (existingDefs.Count > 1)
                            {
                                foreach (var extra in existingDefs.Skip(1))
                                    group.ItemDefinitions.Remove(extra);
                            }

                            var existingDef = existingDefs.FirstOrDefault();
                            if (existingDef != null)
                            {
                                CopyDefinition(entry.DonorDef, existingDef, TargetEpisode);
                            }
                            else
                            {
                                var newDef = new ItemDefinition();
                                CopyDefinition(entry.DonorDef, newDef, TargetEpisode);
                                group.ItemDefinitions.Add(newDef);
                            }
                        }

                        foreach (var type in distinctItems.Select(i => i.Type).Distinct())
                        {
                            var grp = TargetItem.ItemGroups[type];
                            var sorted = grp.ItemDefinitions
                                .GroupBy(d => d.ItemTypeId)
                                .Select(g => g.First())
                                .OrderBy(d => d.ItemTypeId)
                                .ToList();
                            grp.ItemDefinitions.Clear();
                            grp.ItemDefinitions.AddRange(sorted);
                        }

                        string itemSDataDir = Path.Combine(patchTempDir, "Item");
                        Directory.CreateDirectory(itemSDataDir);
                        string itemSDataPath = Path.Combine(itemSDataDir, "Item.SData");

                        byte[] rawBytes = TargetItem.GetBytes(TargetEpisode).ToArray();
                        finalItemSDataBytes = TargetWasEncrypted
                            ? SData.Encrypt(rawBytes, SDataVersion.Regular)
                            : rawBytes;

                        File.WriteAllBytes(itemSDataPath, finalItemSDataBytes);
                        Log($"Item.SData preparado ({finalItemSDataBytes.Length} bytes | Criptografia: {(TargetWasEncrypted ? "Ativa" : "Descriptografada")}).");
                    }

                    // 2. Extrair Modelos 3D (.3DO / .3DC), Texturas (.DDS / .TGA) e Ícones
                    if (copyAssets || copyIcons)
                    {
                        int assetCount = ExtractAllRequiredAssets(DonorData, TargetData, distinctItems, copyAssets, copyIcons, patchTempDir);
                        Log($"Total de {assetCount} arquivos visuais (3D, texturas DDS e ícones) extraídos para o patch.");
                    }

                    // 3. Criar backup do data.sah e aplicar patch no cliente
                    string[] patchFiles = Directory.GetFiles(patchTempDir, "*", SearchOption.AllDirectories);
                    if (patchFiles.Length > 0)
                    {
                        string origBaselineSah = $"{targetSahPath}.bak_ORIGINAL";
                        if (!File.Exists(origBaselineSah))
                            File.Copy(targetSahPath, origBaselineSah, overwrite: false);

                        string sahBackupPath = $"{targetSahPath}.bak_{DateTime.Now:yyyyMMdd_HHmmss}";
                        File.Copy(targetSahPath, sahBackupPath, overwrite: true);
                        Log($"Backup de segurança do data.sah salvo em: {Path.GetFileName(sahBackupPath)}");

                        Log($"Empacotando {patchFiles.Length} arquivos e aplicando no cliente destino...");
                        var patchData = DataBuilder.CreateFromDirectory(patchTempDir, patchOutDir);
                        using var patcher = new DataPatcher();
                        patcher.Patch(TargetData, patchData, null);

                        if (patchData is IDisposable dispPatch)
                            dispPatch.Dispose();

                        Log("Cliente atualizado com sucesso!");
                    }

                    // 3.1 Sincronizar Item.SData solto na pasta do servidor
                    if (finalItemSDataBytes != null)
                    {
                        SyncServerSideItemSData(targetSahPath, finalItemSDataBytes);
                    }

                    int maxItemNameLen = 30;

                    // 4. Executar Gravação no SQL Server com Transação Atômica
                    if (updateSql && !string.IsNullOrWhiteSpace(sqlConnectionString))
                    {
                        Log("Conectando ao SQL Server e gravando itens em PS_GameDefs.dbo.Items...");
                        using var conn = new SqlConnection(sqlConnectionString);
                        conn.Open();

                        maxItemNameLen = GetItemsNameMaxLength(conn);
                        Log($"Tamanho máximo da coluna Items.ItemName detectado: {maxItemNameLen} caracteres.");

                        using var tx = conn.BeginTransaction();

                        try
                        {
                            int cashCount = 0;

                            for (int i = 0; i < distinctItems.Count; i++)
                            {
                                var entry = distinctItems[i];
                                ExecuteSqlUpsert(conn, tx, entry.DonorDef, maxItemNameLen);

                                if (CashItemTypes.Contains(entry.Type))
                                {
                                    ExecuteProductListUpsert(conn, tx, entry.DonorDef);
                                    cashCount++;
                                }

                                OnProgress?.Invoke(i + 1, totalCount);
                            }

                            tx.Commit();
                            Log("Todos os itens foram gravados no SQL Server com sucesso!");
                            if (cashCount > 0)
                                Log($"{cashCount} itens de cash shop atualizados na tabela ProductList.");
                        }
                        catch (Exception ex)
                        {
                            tx.Rollback();
                            Log($"ERRO SQL (Rollback executado): {ex.Message}");
                            throw;
                        }
                    }

                    // 5. Gerar arquivo .SQL de backup completo
                    if (generateSqlBackup)
                    {
                        Log("Gerando arquivo de script SQL de backup...");
                        var sb = new StringBuilder();
                        sb.AppendLine("-- ================================================================");
                        sb.AppendLine($"-- Shaiya Item Importer - Script Gerado em {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                        sb.AppendLine($"-- Total de Itens Únicos: {distinctItems.Count}");
                        sb.AppendLine("-- ================================================================\nUSE [PS_GameDefs];\nGO\n");

                        foreach (var entry in distinctItems)
                        {
                            sb.AppendLine(BuildSqlUpsertScript(entry.DonorDef, maxItemNameLen));
                            if (CashItemTypes.Contains(entry.Type))
                            {
                                sb.AppendLine(BuildProductListUpsertScript(entry.DonorDef));
                            }
                        }

                        File.WriteAllText(backupSqlPath, sb.ToString(), Encoding.UTF8);
                        Log($"Script SQL salvo em: {backupSqlPath}");
                    }

                    Log($"Processo concluído com êxito! {distinctItems.Count} itens importados.");
                    return distinctItems.Count;
                }
                finally
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            });
        }

        private static int GetItemsNameMaxLength(SqlConnection conn)
        {
            try
            {
                using var cmd = new SqlCommand(
                    "SELECT CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'Items' AND COLUMN_NAME = 'ItemName'",
                    conn);
                object? res = cmd.ExecuteScalar();
                if (res != null && res != DBNull.Value && int.TryParse(res.ToString(), out int len) && len > 0)
                    return len;
            }
            catch { }
            return 30;
        }

        private static string TruncateSafe(string? value, int maxLen)
        {
            if (string.IsNullOrEmpty(value)) return "";
            string clean = value.Replace("\0", "").Trim();
            return clean.Length <= maxLen ? clean : clean.Substring(0, maxLen);
        }

        private List<SFile> CollectRequiredAssetFiles(
            Data donorData,
            Data targetData,
            List<ItemEntry> selectedItems,
            bool copyAssets,
            bool copyIcons)
        {
            // ETAPA 1: Indexar todos os arquivos do cliente doador por extensão
            var targetNormalizedPaths = new HashSet<string>(
                targetData.FileIndex.Values.Select(f => NormalizeRelPath(f.RelativePath)),
                StringComparer.OrdinalIgnoreCase);

            var all3DModels = new List<(SFile File, string NormPath)>();
            var allTextures = new List<(SFile File, string NormPath)>();
            var texturesByFileName = new Dictionary<string, List<(SFile File, string NormPath)>>(StringComparer.OrdinalIgnoreCase);
            var texturesByStem = new Dictionary<string, List<(SFile File, string NormPath)>>(StringComparer.OrdinalIgnoreCase);
            var allIconSheets = new List<(SFile File, string NormPath)>();

            foreach (var f in donorData.FileIndex.Values)
            {
                string normPath = NormalizeRelPath(f.RelativePath);
                string ext = Path.GetExtension(f.Name);

                if (ext.Equals(".3do", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".3dc", StringComparison.OrdinalIgnoreCase))
                {
                    all3DModels.Add((f, normPath));
                }
                else if (ext.Equals(".dds", StringComparison.OrdinalIgnoreCase) ||
                         ext.Equals(".tga", StringComparison.OrdinalIgnoreCase))
                {
                    allTextures.Add((f, normPath));

                    if (!texturesByFileName.TryGetValue(f.Name, out var nameList))
                    {
                        nameList = new List<(SFile, string)>();
                        texturesByFileName[f.Name] = nameList;
                    }
                    nameList.Add((f, normPath));

                    string stem = Path.GetFileNameWithoutExtension(f.Name);
                    if (!texturesByStem.TryGetValue(stem, out var stemList))
                    {
                        stemList = new List<(SFile, string)>();
                        texturesByStem[stem] = stemList;
                    }
                    stemList.Add((f, normPath));

                    if (normPath.StartsWith("interface/icon/", StringComparison.OrdinalIgnoreCase) ||
                        (normPath.StartsWith("interface/", StringComparison.OrdinalIgnoreCase) &&
                         f.Name.Contains("icon", StringComparison.OrdinalIgnoreCase)))
                    {
                        allIconSheets.Add((f, normPath));
                    }
                }
            }

            Log($"Indexação por extensão no doador: {all3DModels.Count} modelos (.3DO/.3DC) | {allTextures.Count} texturas (.DDS/.TGA) | {allIconSheets.Count} folhas de ícones.");

            // ETAPA 2: Filtrar e selecionar os arquivos desejados
            var selectedFiles = new Dictionary<string, SFile>(StringComparer.OrdinalIgnoreCase);

            if (copyAssets)
            {
                var wantedModels = new HashSet<int>(selectedItems.Where(i => i.Model > 0).Select(i => i.Model));
                var modelRegexes = wantedModels
                    .Select(m => new Regex($@"(?:^|[_\-])0*{m}(?:[_\-.]|$)", RegexOptions.IgnoreCase))
                    .ToList();

                var chosen3DModels = new List<(SFile File, string NormPath)>();

                foreach (var entry in all3DModels)
                {
                    // Ignora monstros e NPCs estáticos
                    if (entry.NormPath.StartsWith("Character/Monster/", StringComparison.OrdinalIgnoreCase) ||
                        entry.NormPath.StartsWith("Character/Npc/", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    bool isEquipmentFolder =
                        entry.NormPath.StartsWith("Item/", StringComparison.OrdinalIgnoreCase) ||
                        entry.NormPath.StartsWith("Character/", StringComparison.OrdinalIgnoreCase);

                    if (!isEquipmentFolder)
                        continue;

                    // Seleciona se: (1) não existe no cliente Core OU (2) bate com algum Model ID importado
                    bool isExclusiveInDonor = !targetNormalizedPaths.Contains(entry.NormPath);
                    bool matchesItemModel = modelRegexes.Any(rx => rx.IsMatch(entry.File.Name));

                    if (isExclusiveInDonor || matchesItemModel)
                    {
                        if (selectedFiles.TryAdd(entry.NormPath, entry.File))
                        {
                            chosen3DModels.Add(entry);
                        }
                    }
                }

                // Para cada .3DO/.3DC escolhido, puxa automaticamente todas as texturas referenciadas dentro dele + mesmo stem
                foreach (var modelEntry in chosen3DModels)
                {
                    string[] segments = modelEntry.NormPath.Split('/');
                    string? rootFolder = segments.Length >= 2 ? $"{segments[0]}/{segments[1]}/" : null;

                    // 1) Texturas embutidas no arquivo 3D
                    byte[] modelBytes = donorData.GetFileBuffer(modelEntry.File);
                    foreach (string texName in ExtractEmbeddedTextureNames(modelBytes))
                    {
                        if (texturesByFileName.TryGetValue(texName, out var matches))
                        {
                            foreach (var tex in matches)
                            {
                                if (rootFolder != null && matches.Count > 1 &&
                                    !tex.NormPath.StartsWith(rootFolder, StringComparison.OrdinalIgnoreCase))
                                {
                                    continue;
                                }
                                selectedFiles.TryAdd(tex.NormPath, tex.File);
                            }
                        }
                    }

                    // 2) Texturas com o mesmo nome base (ex.: 03_F_201.3DO -> 03_F_201.dds)
                    string modelStem = Path.GetFileNameWithoutExtension(modelEntry.File.Name);
                    if (texturesByStem.TryGetValue(modelStem, out var stemMatches))
                    {
                        foreach (var tex in stemMatches)
                        {
                            selectedFiles.TryAdd(tex.NormPath, tex.File);
                        }
                    }
                }
            }

            if (copyIcons)
            {
                var requiredSheetNumbers = new HashSet<int>();
                foreach (var itm in selectedItems)
                {
                    if (itm.Icon > 0)
                    {
                        requiredSheetNumbers.Add(itm.Icon);
                        int zeroBased = itm.Icon / 256;
                        if (zeroBased > 0)
                        {
                            requiredSheetNumbers.Add(zeroBased);
                            requiredSheetNumbers.Add(zeroBased + 1);
                        }
                    }
                }

                foreach (var iconEntry in allIconSheets)
                {
                    var m = Regex.Match(iconEntry.File.Name, @"(\d+)\.dds$", RegexOptions.IgnoreCase);
                    bool isUsedSheet = m.Success && int.TryParse(m.Groups[1].Value, out int num) && requiredSheetNumbers.Contains(num);
                    bool isMissingInTarget = !targetNormalizedPaths.Contains(iconEntry.NormPath);

                    if (isUsedSheet || isMissingInTarget)
                    {
                        selectedFiles.TryAdd(iconEntry.NormPath, iconEntry.File);
                    }
                }
            }

            return selectedFiles.Values.ToList();
        }

        private int ExtractAllRequiredAssets(
            Data donorData,
            Data targetData,
            List<ItemEntry> selectedItems,
            bool copyAssets,
            bool copyIcons,
            string patchTempDir)
        {
            Log("Coletando modelos 3D, texturas e folhas de ícones por extensão...");

            var filesToExtract = CollectRequiredAssetFiles(donorData, targetData, selectedItems, copyAssets, copyIcons);
            int modelsCount = 0;
            int texturesCount = 0;
            int iconsCount = 0;

            foreach (var sfile in filesToExtract)
            {
                ExtractFileToPatch(donorData, sfile, patchTempDir);
                string norm = NormalizeRelPath(sfile.RelativePath);

                if (norm.StartsWith("Interface/", StringComparison.OrdinalIgnoreCase))
                    iconsCount++;
                else if (sfile.Name.EndsWith(".3do", StringComparison.OrdinalIgnoreCase) || sfile.Name.EndsWith(".3dc", StringComparison.OrdinalIgnoreCase))
                    modelsCount++;
                else
                    texturesCount++;
            }

            Log($"Detalhamento visual: {modelsCount} modelos 3D | {texturesCount} texturas DDS/TGA | {iconsCount} folhas de ícones.");
            return filesToExtract.Count;
        }

        private static HashSet<string> ExtractEmbeddedTextureNames(byte[] modelBytes)
        {
            var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string ascii = Encoding.ASCII.GetString(modelBytes);

            foreach (Match m in EmbeddedTextureRegex.Matches(ascii))
            {
                string fileName = Path.GetFileName(m.Groups[1].Value.Trim());
                if (!string.IsNullOrWhiteSpace(fileName))
                {
                    results.Add(fileName);
                }
            }

            return results;
        }

        private void SyncServerSideItemSData(string targetSahPath, byte[] itemSDataBytes)
        {
            try
            {
                string? clientDir = Path.GetDirectoryName(targetSahPath);
                string? coreRootDir = clientDir != null ? Directory.GetParent(clientDir)?.FullName : null;

                if (coreRootDir == null || !Directory.Exists(coreRootDir)) return;

                var serverSDataFiles = Directory.GetFiles(coreRootDir, "Item.SData", SearchOption.AllDirectories)
                    .Where(p => !p.Contains(".bak_", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (string serverSData in serverSDataFiles)
                {
                    string origBak = $"{serverSData}.bak_ORIGINAL";
                    if (!File.Exists(origBak))
                        File.Copy(serverSData, origBak, overwrite: false);

                    File.Copy(serverSData, $"{serverSData}.bak_{DateTime.Now:yyyyMMdd_HHmmss}", overwrite: true);
                    File.WriteAllBytes(serverSData, itemSDataBytes);
                    Log($"Item.SData do servidor sincronizado em: {serverSData}");
                }
            }
            catch (Exception ex)
            {
                Log($"Aviso ao sincronizar Item.SData do servidor: {ex.Message}");
            }
        }

        private void ExtractFileToPatch(Data donorData, SFile sfile, string patchTempDir)
        {
            string cleanRel = NormalizeRelPath(sfile.RelativePath).Replace('/', Path.DirectorySeparatorChar);
            string dest = Path.Combine(patchTempDir, cleanRel);
            string? dir = Path.GetDirectoryName(dest);
            if (dir != null) Directory.CreateDirectory(dir);

            byte[] buffer = donorData.GetFileBuffer(sfile);
            File.WriteAllBytes(dest, buffer);
        }

        private static short ClampSmallInt(uint value) => value > 32767u ? (short)32767 : (short)value;
        private static ushort ClampUShortSmallInt(ushort value) => value > 32767 ? (ushort)32767 : value;
        private static ushort ClampUShortSmallInt(uint value) => value > 32767u ? (ushort)32767 : (ushort)value;

        private static (ushort reqWis, ushort reqLuc, ushort grade, byte server, ushort drop, byte special) SanitizeCompatFields(ItemDefinition d)
        {
            ushort safeReqLuc = d.ReqLuc > 500 ? (ushort)0 : ClampUShortSmallInt(d.ReqLuc);
            ushort safeGrade = d.Grade > 50 ? (ushort)0 : ClampUShortSmallInt(d.Grade);
            byte safeServer = d.Server > 1 ? (byte)0 : d.Server;
            ushort safeDrop = d.Drop > 1 ? (ushort)0 : (ushort)d.Drop;
            byte safeSpecial = d.Special > 100 ? (byte)0 : d.Special;

            bool isPhysicalOnly = (d.AttackFighter == 1 || d.DefenseFighter == 1) && d.AttackMage == 0 && d.DefenseMage == 0;
            ushort safeReqWis = (isPhysicalOnly && d.ItemType <= 15) ? (ushort)0 : ClampUShortSmallInt(d.ReqWis);

            return (safeReqWis, safeReqLuc, safeGrade, safeServer, safeDrop, safeSpecial);
        }

        private void CopyDefinition(ItemDefinition src, ItemDefinition dest, Episode targetEpisode)
        {
            var (safeReqWis, safeReqLuc, safeGrade, safeServer, safeDrop, safeSpecial) = SanitizeCompatFields(src);

            dest.Name = (src.Name ?? "").Replace("\0", "").Trim();
            dest.Description = (src.Description ?? "").Replace("\0", "").Trim();
            dest.ItemType = src.ItemType;
            dest.ItemTypeId = src.ItemTypeId;
            dest.Model = src.Model;
            dest.Icon = src.Icon;
            dest.MinLevel = src.MinLevel;
            dest.Country = (targetEpisode == Episode.EP5 || targetEpisode == Episode.EP6) && src.Country > 6
                ? (byte)0
                : src.Country;
            dest.AttackFighter = src.AttackFighter;
            dest.DefenseFighter = src.DefenseFighter;
            dest.PatrolRogue = src.PatrolRogue;
            dest.ShootRogue = src.ShootRogue;
            dest.AttackMage = src.AttackMage;
            dest.DefenseMage = src.DefenseMage;
            dest.Grow = src.Grow;
            dest.ReqStr = ClampUShortSmallInt(src.ReqStr);
            dest.ReqDex = ClampUShortSmallInt(src.ReqDex);
            dest.ReqRec = ClampUShortSmallInt(src.ReqRec);
            dest.ReqInt = ClampUShortSmallInt(src.ReqInt);
            dest.ReqWis = safeReqWis;
            dest.ReqLuc = safeReqLuc;
            dest.ReqVg = ClampUShortSmallInt(src.ReqVg);
            dest.ReqOg = src.ReqOg;
            dest.ReqIg = src.ReqIg;
            dest.Range = ClampUShortSmallInt(src.Range);
            dest.AttackTime = src.AttackTime;
            dest.Attrib = src.Attrib;
            dest.Special = safeSpecial;
            dest.Slot = src.Slot;
            dest.Quality = ClampUShortSmallInt(src.Quality);
            dest.Attack = ClampUShortSmallInt(src.Attack);
            dest.AttackAdd = ClampUShortSmallInt(src.AttackAdd);
            dest.Def = ClampUShortSmallInt(src.Def);
            dest.Resist = ClampUShortSmallInt(src.Resist);
            dest.Hp = ClampUShortSmallInt(src.Hp);
            dest.Sp = ClampUShortSmallInt(src.Sp);
            dest.Mp = ClampUShortSmallInt(src.Mp);
            dest.Str = ClampUShortSmallInt(src.Str);
            dest.Dex = ClampUShortSmallInt(src.Dex);
            dest.Rec = ClampUShortSmallInt(src.Rec);
            dest.Int = ClampUShortSmallInt(src.Int);
            dest.Wis = ClampUShortSmallInt(src.Wis);
            dest.Luc = ClampUShortSmallInt(src.Luc);
            dest.Speed = src.Speed;
            dest.Exp = src.Exp;
            dest.BuyPrice = src.BuyPrice;
            dest.SellPrice = src.SellPrice;
            dest.Grade = safeGrade;
            dest.Drop = safeDrop;
            dest.Server = safeServer;
            dest.Count = src.Count == 0 ? (byte)1 : src.Count;
        }

        private void ExecuteSqlUpsert(SqlConnection conn, SqlTransaction tx, ItemDefinition d, int maxNameLen)
        {
            int itemId = d.ItemType * 1000 + d.ItemTypeId;
            byte safeCountry = (TargetEpisode == Episode.EP5 || TargetEpisode == Episode.EP6) && d.Country > 6 ? (byte)0 : d.Country;
            var (safeReqWis, safeReqLuc, safeGrade, safeServer, safeDrop, safeSpecial) = SanitizeCompatFields(d);
            string safeName = TruncateSafe(d.Name, maxNameLen);
            byte safeCount = d.Count == 0 ? (byte)1 : d.Count;

            string query = @"
DELETE FROM Items WHERE Type = @Type AND TypeID = @TypeID AND ItemID <> @ItemID;

IF EXISTS (SELECT 1 FROM Items WHERE ItemID = @ItemID)
BEGIN
    UPDATE Items SET
        ItemName = @ItemName, Type = @Type, TypeID = @TypeID, Reqlevel = @Reqlevel, Country = @Country,
        Attackfighter = @Attackfighter, Defensefighter = @Defensefighter,
        Patrolrogue = @Patrolrogue, Shootrogue = @Shootrogue,
        Attackmage = @Attackmage, Defensemage = @Defensemage, Grow = @Grow,
        ReqStr = @ReqStr, ReqDex = @ReqDex, ReqRec = @ReqRec, ReqInt = @ReqInt, ReqWis = @ReqWis, Reqluc = @Reqluc,
        ReqVg = @ReqVg, ReqOg = @ReqOg, ReqIg = @ReqIg, Range = @Range, AttackTime = @AttackTime,
        Attrib = @Attrib, Special = @Special, Slot = @Slot, Quality = @Quality,
        Effect1 = @Effect1, Effect2 = @Effect2, Effect3 = @Effect3, Effect4 = @Effect4,
        ConstHP = @ConstHP, ConstSP = @ConstSP, ConstMP = @ConstMP,
        ConstStr = @ConstStr, ConstDex = @ConstDex, ConstRec = @ConstRec, ConstInt = @ConstInt, ConstWis = @ConstWis, ConstLuc = @ConstLuc,
        Speed = @Speed, Exp = @Exp, Buy = @Buy, Sell = @Sell, Grade = @Grade, [Drop] = @Drop, Server = @Server, Count = @Count
    WHERE ItemID = @ItemID;
END
ELSE
BEGIN
    INSERT INTO Items (
        ItemID, ItemName, Type, TypeID, Reqlevel, Country,
        Attackfighter, Defensefighter, Patrolrogue, Shootrogue, Attackmage, Defensemage, Grow,
        ReqStr, ReqDex, ReqRec, ReqInt, ReqWis, Reqluc, ReqVg, ReqOg, ReqIg, Range, AttackTime,
        Attrib, Special, Slot, Quality, Effect1, Effect2, Effect3, Effect4,
        ConstHP, ConstSP, ConstMP, ConstStr, ConstDex, ConstRec, ConstInt, ConstWis, ConstLuc,
        Speed, Exp, Buy, Sell, Grade, [Drop], Server, Count
    ) VALUES (
        @ItemID, @ItemName, @Type, @TypeID, @Reqlevel, @Country,
        @Attackfighter, @Defensefighter, @Patrolrogue, @Shootrogue, @Attackmage, @Defensemage, @Grow,
        @ReqStr, @ReqDex, @ReqRec, @ReqInt, @ReqWis, @Reqluc, @ReqVg, @ReqOg, @ReqIg, @Range, @AttackTime,
        @Attrib, @Special, @Slot, @Quality, @Effect1, @Effect2, @Effect3, @Effect4,
        @ConstHP, @ConstSP, @ConstMP, @ConstStr, @ConstDex, @ConstRec, @ConstInt, @ConstWis, @ConstLuc,
        @Speed, @Exp, @Buy, @Sell, @Grade, @Drop, @Server, @Count
    );
END";

            using var cmd = new SqlCommand(query, conn, tx);
            cmd.Parameters.AddWithValue("@ItemID", itemId);
            cmd.Parameters.AddWithValue("@ItemName", safeName);
            cmd.Parameters.AddWithValue("@Type", d.ItemType);
            cmd.Parameters.AddWithValue("@TypeID", d.ItemTypeId);
            cmd.Parameters.AddWithValue("@Reqlevel", ClampSmallInt(d.MinLevel));
            cmd.Parameters.AddWithValue("@Country", safeCountry);
            cmd.Parameters.AddWithValue("@Attackfighter", d.AttackFighter);
            cmd.Parameters.AddWithValue("@Defensefighter", d.DefenseFighter);
            cmd.Parameters.AddWithValue("@Patrolrogue", d.PatrolRogue);
            cmd.Parameters.AddWithValue("@Shootrogue", d.ShootRogue);
            cmd.Parameters.AddWithValue("@Attackmage", d.AttackMage);
            cmd.Parameters.AddWithValue("@Defensemage", d.DefenseMage);
            cmd.Parameters.AddWithValue("@Grow", d.Grow);
            cmd.Parameters.AddWithValue("@ReqStr", ClampSmallInt(d.ReqStr));
            cmd.Parameters.AddWithValue("@ReqDex", ClampSmallInt(d.ReqDex));
            cmd.Parameters.AddWithValue("@ReqRec", ClampSmallInt(d.ReqRec));
            cmd.Parameters.AddWithValue("@ReqInt", ClampSmallInt(d.ReqInt));
            cmd.Parameters.AddWithValue("@ReqWis", (short)safeReqWis);
            cmd.Parameters.AddWithValue("@Reqluc", (short)safeReqLuc);
            cmd.Parameters.AddWithValue("@ReqVg", ClampSmallInt(d.ReqVg));
            cmd.Parameters.AddWithValue("@ReqOg", d.ReqOg);
            cmd.Parameters.AddWithValue("@ReqIg", d.ReqIg);
            cmd.Parameters.AddWithValue("@Range", ClampSmallInt(d.Range));
            cmd.Parameters.AddWithValue("@AttackTime", d.AttackTime);
            cmd.Parameters.AddWithValue("@Attrib", d.Attrib);
            cmd.Parameters.AddWithValue("@Special", safeSpecial);
            cmd.Parameters.AddWithValue("@Slot", d.Slot);
            cmd.Parameters.AddWithValue("@Quality", ClampSmallInt(d.Quality));
            cmd.Parameters.AddWithValue("@Effect1", ClampSmallInt(d.Attack));
            cmd.Parameters.AddWithValue("@Effect2", ClampSmallInt(d.AttackAdd));
            cmd.Parameters.AddWithValue("@Effect3", ClampSmallInt(d.Def));
            cmd.Parameters.AddWithValue("@Effect4", ClampSmallInt(d.Resist));
            cmd.Parameters.AddWithValue("@ConstHP", ClampSmallInt(d.Hp));
            cmd.Parameters.AddWithValue("@ConstSP", ClampSmallInt(d.Sp));
            cmd.Parameters.AddWithValue("@ConstMP", ClampSmallInt(d.Mp));
            cmd.Parameters.AddWithValue("@ConstStr", ClampSmallInt(d.Str));
            cmd.Parameters.AddWithValue("@ConstDex", ClampSmallInt(d.Dex));
            cmd.Parameters.AddWithValue("@ConstRec", ClampSmallInt(d.Rec));
            cmd.Parameters.AddWithValue("@ConstInt", ClampSmallInt(d.Int));
            cmd.Parameters.AddWithValue("@ConstWis", ClampSmallInt(d.Wis));
            cmd.Parameters.AddWithValue("@ConstLuc", ClampSmallInt(d.Luc));
            cmd.Parameters.AddWithValue("@Speed", d.Speed);
            cmd.Parameters.AddWithValue("@Exp", d.Exp);
            cmd.Parameters.AddWithValue("@Buy", (int)Math.Min(d.BuyPrice, (uint)int.MaxValue));
            cmd.Parameters.AddWithValue("@Sell", (int)Math.Min(d.SellPrice, (uint)int.MaxValue));
            cmd.Parameters.AddWithValue("@Grade", (short)safeGrade);
            cmd.Parameters.AddWithValue("@Drop", (short)safeDrop);
            cmd.Parameters.AddWithValue("@Server", safeServer);
            cmd.Parameters.AddWithValue("@Count", safeCount);
            cmd.ExecuteNonQuery();
        }

        private void ExecuteProductListUpsert(SqlConnection conn, SqlTransaction tx, ItemDefinition d)
        {
            int itemId = d.ItemType * 1000 + d.ItemTypeId;
            string productCode = $"Cash_{d.ItemType}_{d.ItemTypeId}";
            string productName = TruncateSafe(string.IsNullOrWhiteSpace(d.Name) ? $"Item {itemId}" : d.Name, 50);
            int buyCost = d.BuyPrice > 0 ? (int)Math.Min(d.BuyPrice, (uint)int.MaxValue) : 100;

            string query = @"
IF EXISTS (SELECT 1 FROM ProductList WHERE ProductCode = @ProductCode)
BEGIN
    UPDATE ProductList SET
        ProductName = @ProductName,
        BuyCost = @BuyCost,
        ItemID1 = @ItemID1,
        ItemCount1 = @ItemCount1
    WHERE ProductCode = @ProductCode;
END
ELSE
BEGIN
    INSERT INTO ProductList (
        ProductName, ProductCode, BuyCost,
        ItemID1, ItemCount1,
        ItemID2, ItemCount2, ItemID3, ItemCount3, ItemID4, ItemCount4,
        ItemID5, ItemCount5, ItemID6, ItemCount6, ItemID7, ItemCount7,
        ItemID8, ItemCount8, ItemID9, ItemCount9, ItemID10, ItemCount10,
        ItemID11, ItemCount11, ItemID12, ItemCount12, ItemID13, ItemCount13,
        ItemID14, ItemCount14, ItemID15, ItemCount15, ItemID16, ItemCount16,
        ItemID17, ItemCount17, ItemID18, ItemCount18, ItemID19, ItemCount19,
        ItemID20, ItemCount20, ItemID21, ItemCount21, ItemID22, ItemCount22,
        ItemID23, ItemCount23, ItemID24, ItemCount24
    ) VALUES (
        @ProductName, @ProductCode, @BuyCost,
        @ItemID1, @ItemCount1,
        0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0,
        0, 0, 0, 0
    );
END";

            using var cmd = new SqlCommand(query, conn, tx);
            cmd.Parameters.AddWithValue("@ProductCode", productCode);
            cmd.Parameters.AddWithValue("@ProductName", productName);
            cmd.Parameters.AddWithValue("@BuyCost", buyCost);
            cmd.Parameters.AddWithValue("@ItemID1", itemId);
            cmd.Parameters.AddWithValue("@ItemCount1", (byte)1);
            cmd.ExecuteNonQuery();
        }

        private string BuildSqlUpsertScript(ItemDefinition d, int maxNameLen)
        {
            int itemId = d.ItemType * 1000 + d.ItemTypeId;
            byte safeCountry = (TargetEpisode == Episode.EP5 || TargetEpisode == Episode.EP6) && d.Country > 6 ? (byte)0 : d.Country;
            var (safeReqWis, safeReqLuc, safeGrade, safeServer, safeDrop, safeSpecial) = SanitizeCompatFields(d);
            string safeName = TruncateSafe(d.Name, maxNameLen).Replace("'", "''");
            byte safeCount = d.Count == 0 ? (byte)1 : d.Count;

            return $@"DELETE FROM Items WHERE Type = {d.ItemType} AND TypeID = {d.ItemTypeId} AND ItemID <> {itemId};
IF EXISTS (SELECT 1 FROM Items WHERE ItemID = {itemId})
    UPDATE Items SET ItemName = '{safeName}', Reqlevel = {ClampSmallInt(d.MinLevel)}, Country = {safeCountry}, Attackfighter = {d.AttackFighter}, Defensefighter = {d.DefenseFighter}, Patrolrogue = {d.PatrolRogue}, Shootrogue = {d.ShootRogue}, Attackmage = {d.AttackMage}, Defensemage = {d.DefenseMage}, Grow = {d.Grow}, ReqStr = {ClampSmallInt(d.ReqStr)}, ReqDex = {ClampSmallInt(d.ReqDex)}, ReqRec = {ClampSmallInt(d.ReqRec)}, ReqInt = {ClampSmallInt(d.ReqInt)}, ReqWis = {safeReqWis}, Reqluc = {safeReqLuc}, ReqVg = {ClampSmallInt(d.ReqVg)}, ReqOg = {d.ReqOg}, ReqIg = {d.ReqIg}, Range = {ClampSmallInt(d.Range)}, AttackTime = {d.AttackTime}, Attrib = {d.Attrib}, Special = {safeSpecial}, Slot = {d.Slot}, Quality = {ClampUShortSmallInt(d.Quality)}, Effect1 = {ClampSmallInt(d.Attack)}, Effect2 = {ClampSmallInt(d.AttackAdd)}, Effect3 = {ClampSmallInt(d.Def)}, Effect4 = {ClampSmallInt(d.Resist)}, ConstHP = {ClampSmallInt(d.Hp)}, ConstSP = {ClampSmallInt(d.Sp)}, ConstMP = {ClampSmallInt(d.Mp)}, ConstStr = {ClampSmallInt(d.Str)}, ConstDex = {ClampSmallInt(d.Dex)}, ConstRec = {ClampSmallInt(d.Rec)}, ConstInt = {ClampSmallInt(d.Int)}, ConstWis = {ClampSmallInt(d.Wis)}, ConstLuc = {ClampSmallInt(d.Luc)}, Speed = {d.Speed}, Exp = {d.Exp}, Buy = {Math.Min(d.BuyPrice, (uint)int.MaxValue)}, Sell = {Math.Min(d.SellPrice, (uint)int.MaxValue)}, Grade = {safeGrade}, [Drop] = {safeDrop}, Server = {safeServer}, Count = {safeCount} WHERE ItemID = {itemId};
ELSE
    INSERT INTO Items (ItemID, ItemName, Type, TypeID, Reqlevel, Country, Attackfighter, Defensefighter, Patrolrogue, Shootrogue, Attackmage, Defensemage, Grow, ReqStr, ReqDex, ReqRec, ReqInt, ReqWis, Reqluc, ReqVg, ReqOg, ReqIg, Range, AttackTime, Attrib, Special, Slot, Quality, Effect1, Effect2, Effect3, Effect4, ConstHP, ConstSP, ConstMP, ConstStr, ConstDex, ConstRec, ConstInt, ConstWis, ConstLuc, Speed, Exp, Buy, Sell, Grade, [Drop], Server, Count)
    VALUES ({itemId}, '{safeName}', {d.ItemType}, {d.ItemTypeId}, {ClampSmallInt(d.MinLevel)}, {safeCountry}, {d.AttackFighter}, {d.DefenseFighter}, {d.PatrolRogue}, {d.ShootRogue}, {d.AttackMage}, {d.DefenseMage}, {d.Grow}, {ClampSmallInt(d.ReqStr)}, {ClampSmallInt(d.ReqDex)}, {ClampSmallInt(d.ReqRec)}, {ClampSmallInt(d.ReqInt)}, {safeReqWis}, {safeReqLuc}, {ClampSmallInt(d.ReqVg)}, {d.ReqOg}, {d.ReqIg}, {ClampSmallInt(d.Range)}, {d.AttackTime}, {d.Attrib}, {safeSpecial}, {d.Slot}, {ClampUShortSmallInt(d.Quality)}, {ClampSmallInt(d.Attack)}, {ClampSmallInt(d.AttackAdd)}, {ClampSmallInt(d.Def)}, {ClampSmallInt(d.Resist)}, {ClampSmallInt(d.Hp)}, {ClampSmallInt(d.Sp)}, {ClampSmallInt(d.Mp)}, {ClampSmallInt(d.Str)}, {ClampSmallInt(d.Dex)}, {ClampSmallInt(d.Rec)}, {ClampSmallInt(d.Int)}, {ClampSmallInt(d.Wis)}, {ClampSmallInt(d.Luc)}, {d.Speed}, {d.Exp}, {Math.Min(d.BuyPrice, (uint)int.MaxValue)}, {Math.Min(d.SellPrice, (uint)int.MaxValue)}, {safeGrade}, {safeDrop}, {safeServer}, {safeCount});";
        }

        private string BuildProductListUpsertScript(ItemDefinition d)
        {
            int itemId = d.ItemType * 1000 + d.ItemTypeId;
            string productCode = $"Cash_{d.ItemType}_{d.ItemTypeId}";
            string safeName = TruncateSafe(string.IsNullOrWhiteSpace(d.Name) ? $"Item {itemId}" : d.Name, 50).Replace("'", "''");
            int buyCost = d.BuyPrice > 0 ? (int)Math.Min(d.BuyPrice, (uint)int.MaxValue) : 100;

            return $@"IF EXISTS (SELECT 1 FROM ProductList WHERE ProductCode = '{productCode}')
    UPDATE ProductList SET ProductName = '{safeName}', BuyCost = {buyCost}, ItemID1 = {itemId}, ItemCount1 = 1 WHERE ProductCode = '{productCode}';
ELSE
    INSERT INTO ProductList (ProductName, ProductCode, BuyCost, ItemID1, ItemCount1, ItemID2, ItemCount2, ItemID3, ItemCount3, ItemID4, ItemCount4, ItemID5, ItemCount5, ItemID6, ItemCount6, ItemID7, ItemCount7, ItemID8, ItemCount8, ItemID9, ItemCount9, ItemID10, ItemCount10, ItemID11, ItemCount11, ItemID12, ItemCount12, ItemID13, ItemCount13, ItemID14, ItemCount14, ItemID15, ItemCount15, ItemID16, ItemCount16, ItemID17, ItemCount17, ItemID18, ItemCount18, ItemID19, ItemCount19, ItemID20, ItemCount20, ItemID21, ItemCount21, ItemID22, ItemCount22, ItemID23, ItemCount23, ItemID24, ItemCount24)
    VALUES ('{safeName}', '{productCode}', {buyCost}, {itemId}, 1, 0,0, 0,0, 0,0, 0,0, 0,0, 0,0, 0,0, 0,0, 0,0, 0,0, 0,0, 0,0, 0,0, 0,0, 0,0, 0,0, 0,0, 0,0, 0,0, 0,0, 0,0, 0,0, 0,0);";
        }

        private string BuildStatsSummary(ItemDefinition d)
        {
            var parts = new List<string>();
            if (d.Attack > 0 || d.AttackAdd > 0) parts.Add($"Atk: {d.Attack}-{d.Attack + d.AttackAdd}");
            if (d.Def > 0) parts.Add($"Def: {d.Def}");
            if (d.Resist > 0) parts.Add($"Res: {d.Resist}");
            if (d.Str > 0) parts.Add($"Str +{d.Str}");
            if (d.Dex > 0) parts.Add($"Dex +{d.Dex}");
            if (d.Rec > 0) parts.Add($"Rec +{d.Rec}");
            if (d.Int > 0) parts.Add($"Int +{d.Int}");
            if (d.Wis > 0) parts.Add($"Wis +{d.Wis}");
            if (d.Luc > 0) parts.Add($"Luc +{d.Luc}");
            if (d.Hp > 0) parts.Add($"HP +{d.Hp}");
            if (d.Slot > 0) parts.Add($"Slots: {d.Slot}");
            return string.Join(" | ", parts);
        }

        public static string GetCategoryName(byte type)
        {
            return type switch
            {
                1 => "Espada 1 Mão",
                2 => "Espada 2 Mãos",
                3 => "Machado 1 Mão",
                4 => "Machado 2 Mãos",
                5 => "Espada Dupla",
                6 => "Lança",
                7 => "Maça 1 Mão",
                8 => "Martelo 2 Mãos",
                9 => "Adaga Reversa",
                10 => "Adaga",
                11 => "Dardo",
                12 => "Cajado",
                13 => "Arco",
                14 => "Besta",
                15 => "Soqueira",
                16 => "Elmo Luz",
                17 => "Peitoral Luz",
                18 => "Calça Luz",
                19 => "Escudo Luz",
                20 => "Luva Luz",
                21 => "Bota Luz",
                22 => "Elmo Fúria",
                23 => "Peitoral Fúria",
                24 => "Calça Fúria",
                25 => "Escudo Fúria",
                26 => "Luva Fúria",
                27 => "Bota Fúria",
                28 => "Anel",
                29 => "Colar/Brinco",
                30 => "Lapis",
                31 => "Poções / Consumíveis",
                32 => "Itens de Missão",
                33 => "Diversos / Craft",
                72 => "Elmo Especial",
                73 => "Peitoral Especial",
                74 => "Calça Especial",
                75 => "Luva Especial",
                76 => "Bota Especial",
                90 => "Capas Especiais",
                91 => "Peitoral Imperial",
                92 => "Botas Imperiais",
                93 => "Luvas Imperiais",
                95 => "Lapísias Especiais",
                100 => "Remédios / Buffs",
                101 => "Runas de Status",
                102 => "Caixas / Recompensas",
                120 => "Mascotes / Pets",
                121 => "Asas",
                150 => "Fantasias / Costumes",
                _ => $"Categoria {type}"
            };
        }

        private void DisposeData()
        {
            if (DonorData is IDisposable d1) d1.Dispose();
            if (TargetData is IDisposable d2) d2.Dispose();
            DonorData = null;
            TargetData = null;
        }

        public void Dispose()
        {
            DisposeData();
            GC.SuppressFinalize(this);
        }
    }
}
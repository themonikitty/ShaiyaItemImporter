using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ShaiyaItemImporter
{
    internal static class Program
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);

        private const int ATTACH_PARENT_PROCESS = -1;

        [STAThread]
        static int Main(string[] args)
        {
            if (args.Contains("--silent"))
            {
                AttachConsole(ATTACH_PARENT_PROCESS);
                return RunSilentAsync(args).GetAwaiter().GetResult();
            }

            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm());
            return 0;
        }

        static async Task<int> RunSilentAsync(string[] args)
        {
            Console.WriteLine("============================================");
            Console.WriteLine("  Shaiya Item Importer - Modo Silencioso");
            Console.WriteLine("============================================");

            string donor = GetArg(args, "--donor") ?? "";
            string target = GetArg(args, "--target") ?? "";
            string sql = GetArg(args, "--sql") ?? "Server=127.0.0.1;Database=PS_GameDefs;User Id=shaiya;Password=shaiya123;TrustServerCertificate=True;";
            string? itemsArg = GetArg(args, "--items");
            string? backupPath = GetArg(args, "--backup");
            bool autoSelectNew = args.Contains("--auto") || itemsArg == null;
            bool forceHigherTypes = args.Contains("--force-type");
            bool copyIcons = !args.Contains("--no-icons");
            bool dryRun = args.Contains("--dry-run");

            bool validateOnly = args.Contains("--validate");
            string? sqlFileArg = GetArg(args, "--sql-file");

            if (validateOnly)
            {
                if (string.IsNullOrEmpty(target) || !File.Exists(target))
                {
                    Console.WriteLine($"ERRO: Arquivo destino não encontrado: {target}");
                    return 1;
                }

                if (string.IsNullOrWhiteSpace(sqlFileArg))
                {
                    string targetDir = Path.GetDirectoryName(target) ?? "";
                    sqlFileArg = Directory.GetFiles(targetDir, "imported_items_*.sql")
                        .OrderByDescending(File.GetLastWriteTime)
                        .FirstOrDefault();
                }

                using var valEngine = new ImporterEngine();
                valEngine.OnLog += msg => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}");
                bool ok = await valEngine.ValidateTargetAsync(target, sql, sqlFileArg);
                return ok ? 0 : 3;
            }

            if (string.IsNullOrEmpty(donor) || !File.Exists(donor))
            {
                Console.WriteLine($"ERRO: Arquivo doador não encontrado: {donor}");
                return 1;
            }
            if (string.IsNullOrEmpty(target) || !File.Exists(target))
            {
                Console.WriteLine($"ERRO: Arquivo destino não encontrado: {target}");
                return 1;
            }

            using var engine = new ImporterEngine();
            engine.OnLog += msg => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}");
            engine.OnProgress += (cur, total) =>
            {
                if (total > 0)
                {
                    int pct = (int)((cur / (double)total) * 100);
                    Console.Write($"\r  Progresso: {pct}% ({cur}/{total})    ");
                    if (cur == total) Console.WriteLine();
                }
            };

            try
            {
                await engine.LoadClientsAsync(donor, target, forceHigherTypes);

                List<ItemEntry> selected;

                if (autoSelectNew)
                {
                    engine.SelectNewAndNonDuplicate(true);
                    selected = engine.LoadedEntries.Where(e => e.IsSelected).ToList();
                    Console.WriteLine($"Modo automático: {selected.Count} itens válidos e compatíveis selecionados.");
                }
                else
                {
                    var requestedIds = new HashSet<string>(
                        itemsArg!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

                    selected = new List<ItemEntry>();
                    foreach (var entry in engine.LoadedEntries)
                    {
                        string key = $"{entry.Type}_{entry.TypeId}";
                        if (requestedIds.Contains(key))
                        {
                            if (entry.Status == ItemImportStatus.IncompatibleType && !forceHigherTypes)
                            {
                                Console.WriteLine($"AVISO: Ignorando [{key}] ({entry.DonorName}) pois Type > {engine.MaxSupportedTargetType}. Use --force-type se seu ps_game suportar.");
                                continue;
                            }
                            entry.IsSelected = true;
                            selected.Add(entry);
                        }
                    }
                }

                if (selected.Count == 0)
                {
                    Console.WriteLine("Nenhum item compatível selecionado. Nada a fazer.");
                    return 0;
                }

                string? limitArg = GetArg(args, "--limit");
                if (limitArg != null && int.TryParse(limitArg, out int limit) && limit > 0 && limit < selected.Count)
                {
                    selected = selected.Take(limit).ToList();
                    Console.WriteLine($"Limitado a {limit} itens para teste.");
                }

                Console.WriteLine();
                Console.WriteLine("Itens a importar:");
                foreach (var item in selected.Take(25))
                {
                    Console.WriteLine($"  [{item.Type:D3}:{item.TypeId:D3}] {item.DonorName,-30} ({item.StatusDisplay})");
                }
                if (selected.Count > 25)
                    Console.WriteLine($"  ... e mais {selected.Count - 25} itens.");
                Console.WriteLine();

                string sqlBackupPath = backupPath ?? Path.Combine(
                    Path.GetDirectoryName(target) ?? "",
                    $"imported_items_{DateTime.Now:yyyyMMdd_HHmmss}.sql");

                int imported = await engine.ExecuteImportAsync(
                    selected,
                    targetSahPath: target,
                    copyAssets: true,
                    copyIcons: copyIcons,
                    updateSData: true,
                    updateSql: true,
                    sqlConnectionString: sql,
                    generateSqlBackup: true,
                    backupSqlPath: sqlBackupPath,
                    dryRun: dryRun);

                Console.WriteLine();
                Console.WriteLine(dryRun
                    ? $"✅ Simulação concluída! {imported} itens prontos para importação."
                    : $"✅ Importação concluída! {imported} itens processados.");
                if (!dryRun)
                    Console.WriteLine($"   Backup SQL: {sqlBackupPath}");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine($"❌ ERRO: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                return 2;
            }
        }

        static string? GetArg(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            }
            return null;
        }
    }
}
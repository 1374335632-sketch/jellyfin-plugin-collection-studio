using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LiteDB;

namespace Jellyfin.Plugin.CollectionStudio.Core
{
    /// <summary>LiteDB 持久层：规则、合集、索引、备份、设置。</summary>
    public sealed class Store : IDisposable
    {
        private readonly LiteDatabase _db;

        public Store(string dbPath)
        {
            var dir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            _db = new LiteDatabase(new ConnectionString { Filename = dbPath, Connection = ConnectionType.Shared });
            FilePath = dbPath;
            Track(_db);
            Rules.EnsureIndex(x => x.Enabled);
            Collections.EnsureIndex(x => x.RuleId);
            Items.EnsureIndex(x => x.Name);
            Backups.EnsureIndex(x => x.CreatedAt);
            RenameLogs.EnsureIndex(x => x.CreatedAt);
        }

        public string FilePath { get; private set; } = string.Empty;

        public ILiteCollection<RuleDoc> Rules => _db.GetCollection<RuleDoc>("rules");

        public ILiteCollection<CollectionDoc> Collections => _db.GetCollection<CollectionDoc>("collections");

        public ILiteCollection<IndexItemDoc> Items => _db.GetCollection<IndexItemDoc>("items");

        public ILiteCollection<BackupDoc> Backups => _db.GetCollection<BackupDoc>("backups");

        public ILiteCollection<RenameLogDoc> RenameLogs => _db.GetCollection<RenameLogDoc>("renameLogs");

        private ILiteCollection<SettingsDoc> SettingsCollection => _db.GetCollection<SettingsDoc>("settings");

        public CsSettings GetSettings()
        {
            var doc = SettingsCollection.FindById("main");
            return doc?.Value ?? new CsSettings();
        }

        public void SaveSettings(CsSettings value)
        {
            SettingsCollection.Upsert(new SettingsDoc { Id = "main", Value = value });
        }

        public List<IndexItemDoc> AllItems() => Items.FindAll().ToList();

        public int CountItems() => Items.Count();

        public void UpsertItems(IEnumerable<IndexItemDoc> items)
        {
            _db.BeginTrans();
            try
            {
                foreach (var it in items) { it.Id = it.ItemId; Items.Upsert(it); }
                _db.Commit();
            }
            catch
            {
                _db.Rollback();
                throw;
            }
        }

        public void RemoveMissingItems(HashSet<string> keep)
        {
            var stale = Items.FindAll().Where(i => !keep.Contains(i.ItemId)).Select(i => i.Id).ToList();
            foreach (var id in stale) Items.Delete(id);
        }

        /// <summary>把被删除的记录写入备份表（删除即备份）。</summary>
        public BackupDoc Backup(string pluginName, string kind, string note)
        {
            var payload = new BackupPayload
            {
                Rules = Rules.FindAll().ToList(),
                Collections = Collections.FindAll().ToList(),
                Settings = GetSettings(),
                Members = Collections.FindAll().ToDictionary(c => c.Id, c => c.ItemIds ?? new List<string>())
            };
            var now = DateTime.Now;
            var name = string.Format("{0}_backup_{1:yyyyMMdd_HHmmss_fff}", pluginName, now);
            var json = System.Text.Json.JsonSerializer.Serialize(payload);
            var doc = new BackupDoc
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = name,
                CreatedAt = now,
                Kind = kind,
                Note = note ?? string.Empty,
                SizeBytes = System.Text.Encoding.UTF8.GetByteCount(json),
                Rules = payload.Rules.Count,
                Collections = payload.Collections.Count,
                Renames = 0,
                Payload = json
            };
            Backups.Insert(doc);
            return doc;
        }

        public BackupPayload ReadPayload(BackupDoc doc)
        {
            return System.Text.Json.JsonSerializer.Deserialize<BackupPayload>(doc.Payload) ?? new BackupPayload();
        }

        public void Dispose()
        {
            try { _db.Dispose(); } catch { }
        }

        /// <summary>登记所有打开的库，卸载插件时统一释放，避免文件句柄占用导致删除失败。</summary>
        private static readonly List<WeakReference<LiteDatabase>> OpenDbs = new List<WeakReference<LiteDatabase>>();

        internal static void Track(LiteDatabase db)
        {
            lock (OpenDbs)
            {
                OpenDbs.RemoveAll(w => !w.TryGetTarget(out var t) || t == null);
                OpenDbs.Add(new WeakReference<LiteDatabase>(db));
            }
        }

        public static void CloseAll()
        {
            lock (OpenDbs)
            {
                foreach (var w in OpenDbs)
                {
                    if (w.TryGetTarget(out var db))
                    {
                        try { db.Dispose(); } catch { }
                    }
                }
                OpenDbs.Clear();
            }

        }
    }
}

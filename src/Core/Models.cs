using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using LiteDB;

namespace Jellyfin.Plugin.CollectionStudio.Core
{
    public sealed class CsSettings
    {
        [JsonPropertyName("metaTubeBaseUrl")] public string MetaTubeBaseUrl { get; set; } = string.Empty;
        [JsonPropertyName("metaTubeToken")] public string MetaTubeToken { get; set; } = string.Empty;
        [JsonPropertyName("metaTubeActorProvider")] public string MetaTubeActorProvider { get; set; } = "GFriends";
        [JsonPropertyName("metaTubeTimeoutSeconds")] public int MetaTubeTimeoutSeconds { get; set; } = 20;
        [JsonPropertyName("customSources")] public List<CustomSource> CustomSources { get; set; } = new List<CustomSource>();
        [JsonPropertyName("defaultPageSize")] public int DefaultPageSize { get; set; } = 50;
        [JsonPropertyName("autoBackupBeforeApply")] public bool AutoBackupBeforeApply { get; set; } = true;
    }

    public sealed class CustomSource
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("urlTemplate")] public string UrlTemplate { get; set; } = string.Empty;
        [JsonPropertyName("imagesPath")] public string ImagesPath { get; set; } = "images[*]";
        [JsonPropertyName("imageBaseUrl")] public string ImageBaseUrl { get; set; } = string.Empty;
        [JsonPropertyName("headers")] public List<HeaderPair> Headers { get; set; } = new List<HeaderPair>();
    }

    public sealed class HeaderPair
    {
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("value")] public string Value { get; set; } = string.Empty;
    }

    public sealed class RuleCondition
    {
        [JsonPropertyName("field")] public string Field { get; set; } = "genre";
        [JsonPropertyName("op")] public string Op { get; set; } = "contains";
        [JsonPropertyName("value")] public string Value { get; set; } = string.Empty;
        [JsonPropertyName("conjunction")] public string Conjunction { get; set; } = "and";
    }

    public sealed class RuleDoc
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("name")] public string Name { get; set; } = "新规则";
        [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
        [JsonPropertyName("mode")] public string Mode { get; set; } = "person";
        [JsonPropertyName("personType")] public string PersonType { get; set; } = "Actor";
        [JsonPropertyName("targetName")] public string TargetName { get; set; } = string.Empty;
        [JsonPropertyName("titleTemplate")] public string TitleTemplate { get; set; } = "{value}";
        [JsonPropertyName("conditions")] public List<RuleCondition> Conditions { get; set; } = new List<RuleCondition>();
        [JsonPropertyName("sortBy")] public string SortBy { get; set; } = "premiereDate";
        [JsonPropertyName("sortDesc")] public bool SortDesc { get; set; }
        [JsonPropertyName("coverMode")] public string CoverMode { get; set; } = "firstItem";
        [JsonPropertyName("coverUrl")] public string CoverUrl { get; set; } = string.Empty;
        [JsonPropertyName("coverProvider")] public string CoverProvider { get; set; } = "MetaTube";
        [JsonPropertyName("minItems")] public int MinItems { get; set; } = 1;
        [JsonPropertyName("maxCollections")] public int MaxCollections { get; set; }
        [JsonPropertyName("refresh")] public bool Refresh { get; set; } = true;
        [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; set; } = DateTime.Now;
        [JsonPropertyName("updatedAt")] public DateTime UpdatedAt { get; set; } = DateTime.Now;

        public RuleDoc Clone()
        {
            return new RuleDoc
            {
                Id = Id, Name = Name, Enabled = Enabled, Mode = Mode, PersonType = PersonType,
                TargetName = TargetName, TitleTemplate = TitleTemplate,
                Conditions = Conditions.ConvertAll(c => new RuleCondition { Field = c.Field, Op = c.Op, Value = c.Value, Conjunction = c.Conjunction }),
                SortBy = SortBy, SortDesc = SortDesc, CoverMode = CoverMode, CoverUrl = CoverUrl, CoverProvider = CoverProvider,
                MinItems = MinItems, MaxCollections = MaxCollections, Refresh = Refresh, CreatedAt = CreatedAt, UpdatedAt = UpdatedAt
            };
        }
    }

    public sealed class CollectionDoc
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("ruleId")] public string RuleId { get; set; } = string.Empty;
        [JsonPropertyName("itemCount")] public int ItemCount { get; set; }
        [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; set; } = DateTime.Now;
        [JsonPropertyName("updatedAt")] public DateTime UpdatedAt { get; set; } = DateTime.Now;
        [JsonPropertyName("coverUrl")] public string CoverUrl { get; set; } = string.Empty;
        [JsonPropertyName("managedBy")] public string ManagedBy { get; set; } = string.Empty;
        [JsonPropertyName("itemIds")] public List<string> ItemIds { get; set; } = new List<string>();
        [JsonPropertyName("groupKey")] public string GroupKey { get; set; } = string.Empty;
    }

    public sealed class SettingsDoc
    {
        [BsonId] public string Id { get; set; } = "main";
        public CsSettings Value { get; set; } = new CsSettings();
    }

    public sealed class IndexItemDoc
    {
        [BsonId] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("itemId")] public string ItemId { get; set; } = string.Empty;
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("originalTitle")] public string OriginalTitle { get; set; } = string.Empty;
        [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
        [JsonPropertyName("kind")] public string Kind { get; set; } = string.Empty;
        [JsonPropertyName("year")] public int? Year { get; set; }
        [JsonPropertyName("premiereDate")] public DateTime? PremiereDate { get; set; }
        [JsonPropertyName("rating")] public double? Rating { get; set; }
        [JsonPropertyName("runtimeMinutes")] public int? RuntimeMinutes { get; set; }
        [JsonPropertyName("officialRating")] public string OfficialRating { get; set; } = string.Empty;
        [JsonPropertyName("studios")] public string[] Studios { get; set; } = Array.Empty<string>();
        [JsonPropertyName("genres")] public string[] Genres { get; set; } = Array.Empty<string>();
        [JsonPropertyName("tags")] public string[] Tags { get; set; } = Array.Empty<string>();
        [JsonPropertyName("actors")] public string[] Actors { get; set; } = Array.Empty<string>();
        [JsonPropertyName("directors")] public string[] Directors { get; set; } = Array.Empty<string>();
        [JsonPropertyName("providerIds")] public Dictionary<string, string> ProviderIds { get; set; } = new Dictionary<string, string>();
        [JsonPropertyName("container")] public string Container { get; set; } = string.Empty;
        [JsonPropertyName("fileName")] public string FileName { get; set; } = string.Empty;
        [JsonPropertyName("indexedAt")] public DateTime IndexedAt { get; set; } = DateTime.Now;
    }

    public sealed class BackupDoc
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; set; } = DateTime.Now;
        [JsonPropertyName("kind")] public string Kind { get; set; } = "manual";
        [JsonPropertyName("note")] public string Note { get; set; } = string.Empty;
        [JsonPropertyName("sizeBytes")] public long SizeBytes { get; set; }
        [JsonPropertyName("rules")] public int Rules { get; set; }
        [JsonPropertyName("collections")] public int Collections { get; set; }
        [JsonPropertyName("renames")] public int Renames { get; set; }
        [JsonPropertyName("payload")] public string Payload { get; set; } = string.Empty;
    }

    public sealed class BackupPayload
    {
        [JsonPropertyName("rules")] public List<RuleDoc> Rules { get; set; } = new List<RuleDoc>();
        [JsonPropertyName("collections")] public List<CollectionDoc> Collections { get; set; } = new List<CollectionDoc>();
        [JsonPropertyName("settings")] public CsSettings? Settings { get; set; }
        [JsonPropertyName("members")] public Dictionary<string, List<string>> Members { get; set; } = new Dictionary<string, List<string>>();
    }

    public sealed class BackupSummary
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("createdAt")] public string CreatedAt { get; set; } = string.Empty;
        [JsonPropertyName("kind")] public string Kind { get; set; } = string.Empty;
        [JsonPropertyName("note")] public string Note { get; set; } = string.Empty;
        [JsonPropertyName("sizeBytes")] public long SizeBytes { get; set; }
        [JsonPropertyName("rules")] public int Rules { get; set; }
        [JsonPropertyName("collections")] public int Collections { get; set; }
        [JsonPropertyName("renames")] public int Renames { get; set; }
    }

    public sealed class RenameRow
    {
        [JsonPropertyName("itemId")] public string ItemId { get; set; } = string.Empty;
        [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
        [JsonPropertyName("originalTitle")] public string OriginalTitle { get; set; } = string.Empty;
        [JsonPropertyName("jellyfinTitle")] public string JellyfinTitle { get; set; } = string.Empty;
        [JsonPropertyName("changed")] public bool Changed { get; set; }
        [JsonPropertyName("kind")] public string Kind { get; set; } = string.Empty;
        [JsonPropertyName("year")] public int? Year { get; set; }
        [JsonPropertyName("premiereDate")] public string PremiereDate { get; set; } = string.Empty;
        [JsonPropertyName("collection")] public string Collection { get; set; } = string.Empty;
    }

    public sealed class PagedRenames
    {
        [JsonPropertyName("total")] public int Total { get; set; }
        [JsonPropertyName("page")] public int Page { get; set; }
        [JsonPropertyName("pageSize")] public int PageSize { get; set; }
        [JsonPropertyName("rows")] public List<RenameRow> Rows { get; set; } = new List<RenameRow>();
    }

    public sealed class CoverCandidate
    {
        [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
        [JsonPropertyName("thumbUrl")] public string ThumbUrl { get; set; } = string.Empty;
    }

    public sealed class CoverSearchResult
    {
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("matchedName")] public string MatchedName { get; set; } = string.Empty;
        [JsonPropertyName("source")] public string Source { get; set; } = string.Empty;
        [JsonPropertyName("candidates")] public List<CoverCandidate> Candidates { get; set; } = new List<CoverCandidate>();
        [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;
    }

    public sealed class FieldInfo
    {
        [JsonPropertyName("key")] public string Key { get; set; } = string.Empty;
        [JsonPropertyName("label")] public string Label { get; set; } = string.Empty;
        [JsonPropertyName("kind")] public string Kind { get; set; } = string.Empty;
        [JsonPropertyName("ops")] public string[] Ops { get; set; } = Array.Empty<string>();
        [JsonPropertyName("groupable")] public bool Groupable { get; set; }
        [JsonPropertyName("multiValue")] public bool MultiValue { get; set; }
    }

    public sealed class PreviewGroup
    {
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("count")] public int Count { get; set; }
        [JsonPropertyName("sample")] public List<PreviewSample> Sample { get; set; } = new List<PreviewSample>();
    }

    public sealed class PreviewSample
    {
        [JsonPropertyName("itemId")] public string ItemId { get; set; } = string.Empty;
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
    }

    public sealed class PreviewResult
    {
        [JsonPropertyName("totalItems")] public int TotalItems { get; set; }
        [JsonPropertyName("groups")] public List<PreviewGroup> Groups { get; set; } = new List<PreviewGroup>();
    }

    public sealed class ApplyResult
    {
        [JsonPropertyName("created")] public int Created { get; set; }
        [JsonPropertyName("updated")] public int Updated { get; set; }
        [JsonPropertyName("skipped")] public int Skipped { get; set; }
        [JsonPropertyName("deleted")] public int Deleted { get; set; }
        [JsonPropertyName("backupId")] public string BackupId { get; set; } = string.Empty;
        [JsonPropertyName("log")] public List<string> Log { get; set; } = new List<string>();
    }
}

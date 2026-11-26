using System;
using Parsec.Shaiya.Item;

namespace ShaiyaItemImporter
{
    public enum ItemImportStatus
    {
        NewItem,
        ReplacePlaceholder,
        AlreadyExists,
        IncompatibleType,
        EmptyOrInvalid
    }

    public class ItemEntry
    {
        public bool IsSelected { get; set; }
        public ItemImportStatus Status { get; set; }
        public byte Type { get; set; }
        public string TypeName { get; set; } = "";
        public byte TypeId { get; set; }
        public int ItemId => Type * 1000 + TypeId;
        public string DonorName { get; set; } = "";
        public string CoreName { get; set; } = "";
        public int ReqLevel { get; set; }
        public int Model { get; set; }
        public int Icon { get; set; }
        public string StatsSummary { get; set; } = "";
        public ItemDefinition DonorDef { get; set; } = null!;
        public ItemDefinition? CoreDef { get; set; }

        public string StatusDisplay => Status switch
        {
            ItemImportStatus.NewItem => "✨ NOVO",
            ItemImportStatus.ReplacePlaceholder => "🔄 PLACEHOLDER",
            ItemImportStatus.AlreadyExists => "✔️ JÁ EXISTE",
            ItemImportStatus.IncompatibleType => "⚠️ TYPE > CORE",
            ItemImportStatus.EmptyOrInvalid => "🚫 VAZIO / INVÁLIDO",
            _ => ""
        };
    }
}
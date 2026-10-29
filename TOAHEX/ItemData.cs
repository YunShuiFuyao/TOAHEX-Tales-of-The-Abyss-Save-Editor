namespace TOAHEX
{
    public class ItemData
    {
        public int Id { get; }
        public string JpName { get; }
        public string CnName { get; }
        /// <summary>英文名（来自英版 _I_.DAT；缺失时回退日文名）。</summary>
        public string EnName { get; set; }

        public string Name
        {
            get
            {
                if (LanguageConfig.Current == Language.EN)
                    return string.IsNullOrEmpty(EnName) ? JpName : EnName;
                return LanguageConfig.Current == Language.JP ? JpName : CnName;
            }
        }
        public int TypeCode { get; }
        public string TypeName { get; }
        public string Category { get; set; }
        public string SubCategory { get; set; }

        public ItemData(int id, string jpName, string cnName, int typeCode, string typeName)
        {
            Id = id;
            JpName = jpName;
            CnName = cnName;
            TypeCode = typeCode;
            TypeName = typeName;
        }

        public ItemData(int id, string jpName, string cnName, int typeCode, string typeName, string category, string subCategory)
            : this(id, jpName, cnName, typeCode, typeName)
        {
            Category = category;
            SubCategory = subCategory;
        }
    }
}

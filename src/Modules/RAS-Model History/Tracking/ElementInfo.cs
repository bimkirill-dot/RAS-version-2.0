namespace RASv2.ModelHistory.Tracking
{
    /// <summary>
    /// Облегчённые сведения об элементе. Хранятся в памяти и в кэше между сеансами (ElementInfoCache), чтобы после
    /// удаления элемента можно было сказать, что именно было удалено и вместе с чем.
    /// </summary>
    public sealed class ElementInfo
    {
        public long Id;
        public string UniqueId;
        public string Category;
        public string ClassName;
        public ObjectGroup Group;
        public string Name;
        public string TypeName;
        public string Level;
        public string Workset;
        public string OwnerView;
        public string Creator;
        public bool IsNoise;
        /// <summary>
        /// От чего элемент зависит и с чем удаляется: основа, вложенность, тип, уровень, вид-владелец, группа, отмеченный
        /// элемент у марки, элементы размера. Нужно, чтобы понять «удалён вместе с …».
        /// </summary>
        public long[] Parents;

        public void FillRecord(ChangeRecord r)
        {
            r.ElementId = Id;
            r.UniqueId = UniqueId;
            r.Category = Category;
            r.ClassName = ClassName;
            r.Group = Group;
            r.Name = Name;
            r.TypeName = TypeName;
            r.Level = Level;
            r.Workset = Workset;
            r.OwnerView = OwnerView;
            if (string.IsNullOrEmpty(r.Creator)) r.Creator = Creator;
            if (IsNoise) r.IsNoise = true;
        }
    }
}

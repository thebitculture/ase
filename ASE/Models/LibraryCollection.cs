namespace ASE.Models
{
    public class LibraryCollection
    {
        public string ASEVersion { get; set; }
        public DateTime LastUpdate { get; set; }
        public List<LibraryItem> Collection { get; set; }
    }
}

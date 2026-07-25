#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace ASE.Models
{
    public class LibraryItem
    {
        public string Id { get; set; } = string.Empty;
        public List<RegionText>? Name { get; set; }
        public List<RegionText>? Synopsis { get; set; }
        public string? Filename { get; set; }
        public string? Developer { get; set; }
        public string? Publisher { get; set; }
        public List<RegionText>? Date { get; set; }
        public List<RegionText>? Genre { get; set; }
        public string? GameMenuId { get; set; }
        public string? GameMenuGroupName { get; set; }
        public string? GameMenuNumber { get; set; }
        public string? GameMenuGroupRelease =>
            GameMenuId != null
                ? $"{GameMenuGroupName} {GameMenuNumber}"
                : null;

        public class RegionText 
        {
            public string? Region { get; set; }

            [JsonPropertyName("text")]
            public string? Text { get; set; }
        }

    }
}

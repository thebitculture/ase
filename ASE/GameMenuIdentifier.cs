using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ASE
{
    /// <summary>
    /// One entry of the GameMenus.json catalog: a game and the menu codes
    /// (group + number + optional suffix) it appears in.
    /// </summary>
    public class GameEntry
    {
        [JsonPropertyName("Name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("GameMenu")]
        public List<string> GameMenu { get; set; } = new();
    }

    /// <summary>
    /// Result of an identification: which game was found and which code/candidate it matched.
    /// </summary>
    public class IdentificationResult
    {
        public string FileName { get; init; } = string.Empty;
        public string GameName { get; init; } = string.Empty;
        public string MatchedCode { get; init; } = string.Empty;
        public string MatchedGroup { get; init; } = string.Empty;
    }

    /// <summary>
    /// Identifies Atari ST games from the name of a disk image file, matching it
    /// against the menu codes of hacker groups (Automation, D-Bug, etc.) present
    /// in the GameMenus.json catalog.
    /// </summary>
    public class GameMenuIdentifier
    {
        // Map of 2-letter ID (as it appears in GameMenus.json) -> full group name.
        private static readonly Dictionary<string, string> Groups = new(StringComparer.OrdinalIgnoreCase)
        {
            { "AU", "Automation" },
            { "CY", "Cynix" },
            { "DB", "D-Bug" },
            { "EM", "Electric Mouse" },
            { "FF", "Flame of Finland" },
            { "FZ", "Fuzion" },
            { "GG", "SuperGAU" },
            { "MB", "Medway Boys" },
            { "PK", "Pompey Pirates (Krappy Compacts)" },
            { "PP", "Pompey Pirates" },
            { "SD", "Sewer Doc Disk" },
            { "SO", "Spaced Out" },
            { "SU", "Superior" },
            { "TE", "T. E. R. Doc Disk" },
            { "VE", "Vectronix" },
        };

        // ALTERNATIVE abbreviations may use that match neither the
        // catalog's 2-letter ID nor the full group name.
        // Key: alternative abbreviation.
        // Value: the group's 2-letter ID in GameMenus.json (e.g. "FF").
        private static readonly Dictionary<string, string> AbbreviationAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            // Aliases for Automation
            { "AUTO", "AU" },
            // Aliases for Flame of Finland
            { "FOF", "FF" },
            // Aliases for D-BUG
            { "DBUG", "DB" },
            // Aliases for The Medway Boys
            { "TMB", "MB" },
            { "MEDWAY", "MB" },
            // Pompey Pirates (Krappy Compacts)
            { "PPK", "PK" },
            { "KRAPPY", "PK" },
            { "PPKC", "PK" },
            { "KC", "PK" },
            // Pompey Pirates
            { "POMPEY", "PP" },
            // SuperGAU
            { "SG", "GG" },
            { "SGAU", "GG" },
            // Electric Mouse
            { "EMOUSE", "EM" },
            // Sewer Doc Disk
            { "SEWERDOC", "SD" },
            { "SDD", "SD" },
            // Spaced Out
            { "SPACED", "SO" },
            { "PSOU", "SO" },
            // T. E. R. Doc Disk
            { "TERDOC", "TE" },
            { "TERDD", "TE" },
            // Vectronix
            { "VECT", "VE" },
        };

        // Reverse index: group ID -> list of its alternative abbreviations.
        // Computed once from AbbreviationAliases.
        private static readonly Dictionary<string, List<string>> AliasesByGroupId = BuildAliasesByGroupId();

        private static Dictionary<string, List<string>> BuildAliasesByGroupId()
        {
            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in AbbreviationAliases)
            {
                if (!result.TryGetValue(pair.Value, out var list))
                {
                    list = new List<string>();
                    result[pair.Value] = list;
                }
                list.Add(pair.Key);
            }
            return result;
        }

        // Codes with the format: 2 group letters + number + optional letter suffix (A, B, C...)
        private static readonly Regex CodePattern = new(@"^([A-Za-z]{2})(\d+)([A-Za-z]*)$", RegexOptions.Compiled);

        // Parenthesized content (e.g. "(Disk 1)", "(Copy)", "(v2)") to ignore entirely
        // when normalizing, so that "au001 (v2).st" still matches "au001".
        private static readonly Regex ParenthesesPattern = new(@"\([^()]*\)", RegexOptions.Compiled);

        // "(Part A)", "(Part B)"... indicates the disk suffix (equivalent to the catalog's
        // "AU499A", "AU499B" codes). Captured BEFORE the parentheses are removed.
        private static readonly Regex PartSuffixPattern = new(@"\(\s*Part\s+([A-Za-z])\s*\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // "(Disk 1 of 2)", "(Disk 2 of 2)"... indicates the same suffix but as a disk
        // number instead of a letter: disk 1 -> A, disk 2 -> B, disk 3 -> C, etc.
        private static readonly Regex DiskOfPattern = new(@"\(\s*Disk\s+(\d+)\s+of\s+\d+\s*\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Loose words (outside the parentheses) that add nothing to the identifier,
        // typical of names like "Automation Menu Disk 499 (...)".
        private static readonly Regex FillerWordPattern = new(@"\b(disk)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly List<GameEntry> _catalog;

        // Inverted index: normalized name variant -> list of (game, code) pairs that produce it.
        // Built once at load time, so the per-file lookup is O(1) instead of walking
        // the ~23,000 codes every time.
        private readonly Dictionary<string, List<(GameEntry Game, string Code)>> _index = new();

        public GameMenuIdentifier(List<GameEntry> catalog)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            BuildIndex();
        }

        /// <summary>
        /// Loads the catalog from a GameMenus.json file and returns the identifier ready to use.
        /// </summary>
        public static GameMenuIdentifier LoadFromFile(string jsonPath)
        {
            if (!File.Exists(jsonPath))
                return new GameMenuIdentifier(new List<GameEntry>());

            string json = File.ReadAllText(jsonPath);
            
            if (string.IsNullOrEmpty(json))
                return new GameMenuIdentifier(new List<GameEntry>());

            var catalog = JsonSerializer.Deserialize<List<GameEntry>>(json)
                           ?? new List<GameEntry>();
            return new GameMenuIdentifier(catalog);
        }

        /// <summary>
        /// Tries to identify a game from the path (or just the name) of a disk image file.
        /// </summary>
        public IdentificationResult? Identify(string filePath)
        {
            string nameNoExt = Path.GetFileNameWithoutExtension(filePath);
            string normalized = Normalize(nameNoExt);

            return _index.TryGetValue(normalized, out var matches) && matches.Count > 0
                ? ToResult(nameNoExt, matches[0])
                : null;
        }

        /// <summary>
        /// Same as Identify, but returns ALL the matches (in case the same normalized
        /// name could correspond to more than one game/code).
        /// </summary>
        public List<IdentificationResult> IdentifyAll(string filePath)
        {
            string nameNoExt = Path.GetFileNameWithoutExtension(filePath);
            string normalized = Normalize(nameNoExt);

            if (!_index.TryGetValue(normalized, out var matches))
                return new List<IdentificationResult>();

            return matches.Select(m => ToResult(nameNoExt, m)).ToList();
        }

        #region Internals

        private void BuildIndex()
        {
            foreach (var game in _catalog)
            {
                foreach (var code in game.GameMenu)
                {
                    foreach (var candidate in BuildCandidates(code))
                    {
                        if (!_index.TryGetValue(candidate, out var list))
                        {
                            list = new List<(GameEntry, string)>();
                            _index[candidate] = list;
                        }
                        list.Add((game, code));
                    }
                }
            }
        }

        /// <summary>
        /// Generates the (already normalized) filename variants for a menu code,
        /// e.g. "AU001" -> { "au001", "automation001", "automationmenu001" }, plus one
        /// variant for each alternative abbreviation registered in AbbreviationAliases
        /// (e.g. "FF184" -> also "fof184" thanks to the "FOF" -> "FF" alias).
        /// </summary>
        private static IEnumerable<string> BuildCandidates(string code)
        {
            var match = CodePattern.Match(code);
            if (!match.Success)
                yield break;

            string id = match.Groups[1].Value;
            string number = match.Groups[2].Value;
            string suffix = match.Groups[3].Value;

            yield return Normalize($"{id}{number}{suffix}");

            if (Groups.TryGetValue(id, out var groupName))
            {
                string groupNorm = Normalize(groupName);
                yield return Normalize($"{groupNorm}{number}{suffix}");
                yield return Normalize($"{groupNorm}menu{number}{suffix}");
            }

            if (AliasesByGroupId.TryGetValue(id, out var aliases))
            {
                foreach (var alias in aliases)
                {
                    yield return Normalize($"{alias}{number}{suffix}");
                }
            }
        }

        /// <summary>
        /// Normalizes a name: lowercase and alphanumeric characters only
        /// (removes spaces, hyphens, dots, underscores, etc.), also resolving the
        /// disk suffix from "(Part X)" or "(Disk N of M)" when present.
        /// </summary>
        private static string Normalize(string s)
        {
            // 1. Determine the disk suffix from "(Part A)" or, failing that, from
            //    "(Disk N of M)" (N=1 -> A, N=2 -> B, ...). If both appear
            //    (as in "...(Disk 1 of 2)(Part A)"), "(Part X)" takes priority and
            //    the suffix is only appended once.
            string partSuffix = string.Empty;

            var partMatch = PartSuffixPattern.Match(s);
            if (partMatch.Success)
            {
                partSuffix = partMatch.Groups[1].Value.ToLowerInvariant();
            }
            else
            {
                var diskMatch = DiskOfPattern.Match(s);
                if (diskMatch.Success && int.TryParse(diskMatch.Groups[1].Value, out int diskNumber) && diskNumber >= 1 && diskNumber <= 26)
                {
                    partSuffix = ((char)('a' + diskNumber - 1)).ToString();
                }
            }

            // 2. Remove any "(...)" -- and whatever it contains -- before keeping
            //    only letters/digits. This way "au001 (v2)" and "au001 (Disk 1)"
            //    normalize the same as "au001".
            string withoutParens = ParenthesesPattern.Replace(s, string.Empty);

            // 3. Strip filler words outside the parentheses (e.g. "Disk" in
            //    "Automation Menu Disk 499") that are not part of the generated candidates.
            string withoutFiller = FillerWordPattern.Replace(withoutParens, string.Empty);

            Span<char> buffer = stackalloc char[withoutFiller.Length];
            int count = 0;
            foreach (char c in withoutFiller)
            {
                if (char.IsLetterOrDigit(c))
                    buffer[count++] = char.ToLowerInvariant(c);
            }

            // 4. The suffix goes at the end, just like the letter suffix that codes
            //    such as "AU499A" already carry in the catalog.
            return new string(buffer[..count]) + partSuffix;
        }

        private static IdentificationResult ToResult(string fileName, (GameEntry Game, string Code) match)
        {
            var idPrefix = match.Code.Length >= 2 ? match.Code[..2].ToUpperInvariant() : string.Empty;
            Groups.TryGetValue(idPrefix, out var groupName);

            return new IdentificationResult
            {
                FileName = fileName,
                GameName = match.Game.Name,
                MatchedCode = match.Code,
                MatchedGroup = groupName ?? idPrefix
            };
        }

        #endregion
    }
}
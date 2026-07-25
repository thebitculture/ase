#nullable enable

using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ASE.Models
{
    public class ScreenScraper
    {
        #region Root

        public class JeuInfosResponse
        {
            [JsonPropertyName("header")]
            public Header? Header { get; set; }

            [JsonPropertyName("response")]
            public ResponseBody? Response { get; set; }
        }

        public class Header
        {
            [JsonPropertyName("APIversion")]
            public string? ApiVersion { get; set; }

            [JsonPropertyName("dateTime")]
            public string? DateTime { get; set; }

            [JsonPropertyName("commandRequested")]
            public string? CommandRequested { get; set; }

            [JsonPropertyName("success")]
            public string? Success { get; set; }

            [JsonPropertyName("error")]
            public string? Error { get; set; }

            [JsonIgnore]
            public bool IsSuccess => string.Equals(Success, "true", StringComparison.OrdinalIgnoreCase);
        }

        public class ResponseBody
        {
            [JsonPropertyName("serveurs")]
            public Serveurs? Serveurs { get; set; }

            [JsonPropertyName("ssuser")]
            public SsUser? SsUser { get; set; }

            [JsonPropertyName("jeu")]
            public Jeu? Jeu { get; set; }
        }

        #endregion

        #region Systems list (systemesListe.php)

        public class SystemesListeResponse
        {
            [JsonPropertyName("header")]
            public Header? Header { get; set; }

            [JsonPropertyName("response")]
            public SystemesListeBody? Response { get; set; }
        }

        public class SystemesListeBody
        {
            [JsonPropertyName("serveurs")]
            public Serveurs? Serveurs { get; set; }

            [JsonPropertyName("ssuser")]
            public SsUser? SsUser { get; set; }

            [JsonPropertyName("systemes")]
            public List<Systeme>? Systemes { get; set; }
        }

        public class Systeme
        {
            [JsonPropertyName("id")]
            [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
            public int Id { get; set; }

            /// <summary>Only present on sub-systems (e.g. Sega 32X → Megadrive).</summary>
            [JsonPropertyName("parentid")]
            [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
            public int? ParentId { get; set; }

            [JsonPropertyName("noms")] public SystemeNoms? Noms { get; set; }

            /// <summary>Comma-separated ROM file extensions, e.g. "gen,md,smd,bin,sg".</summary>
            [JsonPropertyName("extensions")] public string? Extensions { get; set; }

            [JsonPropertyName("compagnie")] public string? Compagnie { get; set; }

            /// <summary>Console, Ordinateur, Arcade, Portable…</summary>
            [JsonPropertyName("type")] public string? Type { get; set; }

            [JsonPropertyName("datedebut")] public string? DateDebut { get; set; }
            [JsonPropertyName("datefin")] public string? DateFin { get; set; }

            /// <summary>rom, iso, dossier…</summary>
            [JsonPropertyName("romtype")] public string? RomType { get; set; }

            /// <summary>cartouche, disquette, cd…</summary>
            [JsonPropertyName("supporttype")] public string? SupportType { get; set; }

            [JsonPropertyName("medias")] public List<SystemeMedia>? Medias { get; set; }

            public override string ToString() => Noms?.Eu ?? Id.ToString();
        }

        public class SystemeNoms
        {
            [JsonPropertyName("nom_eu")] public string? Eu { get; set; }
            [JsonPropertyName("nom_us")] public string? Us { get; set; }
            [JsonPropertyName("nom_jp")] public string? Jp { get; set; }
            [JsonPropertyName("nom_recalbox")] public string? Recalbox { get; set; }
            [JsonPropertyName("nom_retropie")] public string? RetroPie { get; set; }
            [JsonPropertyName("nom_launchbox")] public string? LaunchBox { get; set; }
            [JsonPropertyName("nom_hyperspin")] public string? HyperSpin { get; set; }

            /// <summary>Comma-separated list of alternative/common names.</summary>
            [JsonPropertyName("noms_commun")] public string? Commun { get; set; }
        }

        /// <summary>
        /// System media entry. Besides the shared <see cref="Media"/> fields it may carry a
        /// variant name (<see cref="Version"/>) and, depending on the media type, dozens of
        /// layout attributes (bezel screen position, box/support template coordinates,
        /// typography, colors… with inconsistent casing), collected in <see cref="Extra"/>.
        /// </summary>
        public class SystemeMedia : Media
        {
            /// <summary>Variant within a type/region, e.g. support2D "gris"/"rouge" or controls "BTN-A".</summary>
            [JsonPropertyName("version")] public string? Version { get; set; }

            [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
        }

        #endregion

        #region User infos (ssuserInfos.php)

        public class UserInfosResponse
        {
            [JsonPropertyName("header")]
            public Header? Header { get; set; }

            [JsonPropertyName("response")]
            public UserInfosBody? Response { get; set; }
        }

        public class UserInfosBody
        {
            [JsonPropertyName("ssuser")]
            public SsUser? SsUser { get; set; }
        }

        #endregion

        #region Server / user status

        public class Serveurs
        {
            [JsonPropertyName("cpu1")] public string? Cpu1 { get; set; }
            [JsonPropertyName("cpu2")] public string? Cpu2 { get; set; }
            [JsonPropertyName("cpu3")] public string? Cpu3 { get; set; }
            [JsonPropertyName("cpu4")] public string? Cpu4 { get; set; }
            [JsonPropertyName("threadsmin")] public string? ThreadsMin { get; set; }
            [JsonPropertyName("nbscrapeurs")] public string? NbScrapeurs { get; set; }
            [JsonPropertyName("apiacces")] public string? ApiAcces { get; set; }
            [JsonPropertyName("closefornomember")] public string? CloseForNoMember { get; set; }
            [JsonPropertyName("closeforleecher")] public string? CloseForLeecher { get; set; }
            [JsonPropertyName("maxthreadfornonmember")] public string? MaxThreadForNonMember { get; set; }
            [JsonPropertyName("threadfornonmember")] public string? ThreadForNonMember { get; set; }
            [JsonPropertyName("maxthreadformember")] public string? MaxThreadForMember { get; set; }
            [JsonPropertyName("threadformember")] public string? ThreadForMember { get; set; }
        }

        public class SsUser
        {
            [JsonPropertyName("id")] public string? Id { get; set; }
            [JsonPropertyName("numid")] public string? NumId { get; set; }
            [JsonPropertyName("niveau")] public string? Niveau { get; set; }
            [JsonPropertyName("contribution")] public string? Contribution { get; set; }
            [JsonPropertyName("uploadsysteme")] public string? UploadSysteme { get; set; }
            [JsonPropertyName("uploadinfos")] public string? UploadInfos { get; set; }
            [JsonPropertyName("romasso")] public string? RomAsso { get; set; }
            [JsonPropertyName("uploadmedia")] public string? UploadMedia { get; set; }
            [JsonPropertyName("propositionok")] public string? PropositionOk { get; set; }
            [JsonPropertyName("propositionko")] public string? PropositionKo { get; set; }
            [JsonPropertyName("quotarefu")] public string? QuotaRefu { get; set; }
            [JsonPropertyName("maxthreads")] public string? MaxThreads { get; set; }
            [JsonPropertyName("maxdownloadspeed")] public string? MaxDownloadSpeed { get; set; }
            [JsonPropertyName("requeststoday")] public string? RequestsToday { get; set; }
            [JsonPropertyName("requestskotoday")] public string? RequestsKoToday { get; set; }
            [JsonPropertyName("maxrequestspermin")] public string? MaxRequestsPerMin { get; set; }
            [JsonPropertyName("maxrequestsperday")] public string? MaxRequestsPerDay { get; set; }
            [JsonPropertyName("maxrequestskoperday")] public string? MaxRequestsKoPerDay { get; set; }
            [JsonPropertyName("visites")] public string? Visites { get; set; }
            [JsonPropertyName("datedernierevisite")] public string? DateDerniereVisite { get; set; }
            [JsonPropertyName("favregion")] public string? FavRegion { get; set; }
        }

        #endregion

        #region Game

        public class Jeu
        {
            [JsonPropertyName("id")] public string? Id { get; set; }
            [JsonPropertyName("romid")] public string? RomId { get; set; }
            [JsonPropertyName("notgame")] public string? NotGame { get; set; }

            [JsonPropertyName("noms")] public List<RegionText>? Noms { get; set; }

            [JsonPropertyName("cloneof")] public string? CloneOf { get; set; }

            [JsonPropertyName("systeme")] public IdText? Systeme { get; set; }
            [JsonPropertyName("editeur")] public IdText? Editeur { get; set; }
            [JsonPropertyName("developpeur")] public IdText? Developpeur { get; set; }

            [JsonPropertyName("joueurs")] public TextValue? Joueurs { get; set; }
            [JsonPropertyName("note")] public TextValue? Note { get; set; }

            [JsonPropertyName("topstaff")] public string? TopStaff { get; set; }
            [JsonPropertyName("rotation")] public string? Rotation { get; set; }

            [JsonPropertyName("synopsis")] public List<LangueText>? Synopsis { get; set; }
            [JsonPropertyName("dates")] public List<RegionText>? Dates { get; set; }

            [JsonPropertyName("genres")] public List<Groupe>? Genres { get; set; }
            [JsonPropertyName("familles")] public List<Groupe>? Familles { get; set; }

            [JsonPropertyName("medias")] public List<Media>? Medias { get; set; }

            /// <summary>Every known ROM for this game.</summary>
            [JsonPropertyName("roms")] public List<Rom>? Roms { get; set; }

            /// <summary>The specific ROM that matched the request.</summary>
            [JsonPropertyName("rom")] public Rom? Rom { get; set; }
        }

        #endregion

        #region Shared value types

        public class TextValue
        {
            [JsonPropertyName("text")] public string? Text { get; set; }
            public override string ToString() => Text ?? string.Empty;
        }

        public class IdText : TextValue
        {
            [JsonPropertyName("id")] public string? Id { get; set; }
        }

        public class RegionText : TextValue
        {
            [JsonPropertyName("region")] public string? Region { get; set; }
        }

        public class LangueText : TextValue
        {
            [JsonPropertyName("langue")] public string? Langue { get; set; }
        }

        /// <summary>Shared shape for both "genres" and "familles".</summary>
        public class Groupe
        {
            [JsonPropertyName("id")] public string? Id { get; set; }
            [JsonPropertyName("nomcourt")] public string? NomCourt { get; set; }
            [JsonPropertyName("principale")] public string? Principale { get; set; }
            [JsonPropertyName("parentid")] public string? ParentId { get; set; }
            [JsonPropertyName("noms")] public List<LangueText>? Noms { get; set; }
        }

        #endregion

        #region Media

        public class Media
        {
            [JsonPropertyName("id")] public string? Id { get; set; }

            /// <summary>Owner of the media: jeu, editeur, developpeur, joueurs, note, genre, famille…</summary>
            [JsonPropertyName("parent")] public string? Parent { get; set; }

            [JsonPropertyName("subparent")] public string? SubParent { get; set; }

            /// <summary>Media kind: ss, sstitle, fanart, video, wheel, box-2D, manuel…</summary>
            [JsonPropertyName("type")] public string? Type { get; set; }

            [JsonPropertyName("url")] public string? Url { get; set; }
            [JsonPropertyName("region")] public string? Region { get; set; }

            /// <summary>Disk/support number; only present on support-* media types.</summary>
            [JsonPropertyName("support")] public string? Support { get; set; }

            [JsonPropertyName("crc")] public string? Crc { get; set; }
            [JsonPropertyName("md5")] public string? Md5 { get; set; }
            [JsonPropertyName("sha1")] public string? Sha1 { get; set; }
            [JsonPropertyName("size")] public string? Size { get; set; }
            [JsonPropertyName("format")] public string? Format { get; set; }
        }

        #endregion

        #region ROMs

        public class Rom
        {
            [JsonPropertyName("id")] public string? Id { get; set; }
            [JsonPropertyName("romfilename")] public string? RomFileName { get; set; }
            [JsonPropertyName("romsize")] public string? RomSize { get; set; }

            /// <summary>Only present on the singular "rom" node.</summary>
            [JsonPropertyName("romtype")] public string? RomType { get; set; }

            /// <summary>Only present on the singular "rom" node: disquette, cartouche…</summary>
            [JsonPropertyName("romsupporttype")] public string? RomSupportType { get; set; }

            [JsonPropertyName("romnumsupport")] public string? RomNumSupport { get; set; }
            [JsonPropertyName("romtotalsupport")] public string? RomTotalSupport { get; set; }
            [JsonPropertyName("romcloneof")] public string? RomCloneOf { get; set; }

            [JsonPropertyName("romcrc")] public string? RomCrc { get; set; }
            [JsonPropertyName("rommd5")] public string? RomMd5 { get; set; }
            [JsonPropertyName("romsha1")] public string? RomSha1 { get; set; }

            [JsonPropertyName("beta")] public string? Beta { get; set; }
            [JsonPropertyName("demo")] public string? Demo { get; set; }
            [JsonPropertyName("proto")] public string? Proto { get; set; }
            [JsonPropertyName("trad")] public string? Trad { get; set; }
            [JsonPropertyName("hack")] public string? Hack { get; set; }
            [JsonPropertyName("unl")] public string? Unl { get; set; }
            [JsonPropertyName("alt")] public string? Alt { get; set; }
            [JsonPropertyName("best")] public string? Best { get; set; }

            [JsonPropertyName("retroachievement")] public string? RetroAchievement { get; set; }
            [JsonPropertyName("gamelink")] public string? GameLink { get; set; }
            [JsonPropertyName("nbscrap")] public string? NbScrap { get; set; }

            /// <summary>Optional; absent on most ROM entries.</summary>
            [JsonPropertyName("langues")] public Langues? Langues { get; set; }
        }

        /// <summary>Parallel arrays, one entry per language, rather than a list of objects.</summary>
        public class Langues
        {
            [JsonPropertyName("langues_id")] public List<string>? Ids { get; set; }
            [JsonPropertyName("langues_shortname")] public List<string>? ShortNames { get; set; }
            [JsonPropertyName("langues_en")] public List<string>? En { get; set; }
            [JsonPropertyName("langues_fr")] public List<string>? Fr { get; set; }
            [JsonPropertyName("langues_de")] public List<string>? De { get; set; }
            [JsonPropertyName("langues_es")] public List<string>? Es { get; set; }
            [JsonPropertyName("langues_it")] public List<string>? It { get; set; }
            [JsonPropertyName("langues_pt")] public List<string>? Pt { get; set; }
        }

        #endregion
    }
}

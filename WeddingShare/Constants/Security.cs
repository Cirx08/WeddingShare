namespace WeddingShare.Constants
{
    public class Security
    {
        public const string HardenGallerySecurity = "Security:Harden_Gallery_Security";

        public class Encryption
        {
            public const string Key = "Security:Encryption:Key";
            public const string Salt = "Security:Encryption:Salt";
            public const string Iterations = "Security:Encryption:Iterations";
            public const string HashType = "Security:Encryption:HashType";
        }

        public class Headers
        {
            public const string Enabled = "Security:Headers:Enabled";
            public const string XFrameOptions = "Security:Headers:X_Frame_Options";
            public const string XContentTypeOptions = "Security:Headers:X_Content_Type_Options";
            public const string CSP = "Security:Headers:CSP";
        }

        public class MultiFactor
        {
            public const string ResetToDefault = "Security:2FA:Reset_To_Default";
        }
    }
}
using Microsoft.AspNetCore.Localization;
using System.Globalization;
using WeddingShare.Helpers;

namespace WeddingShare.Configurations
{
    public static class LocalizationConfiguration
    {
        public static string CurrentCulture = "en-GB";

        public static void AddLocalizationConfiguration(this IServiceCollection services, ConfigHelper config)
        {
            services.AddLocalization(options =>
            {
                options.ResourcesPath = "Resources";
            });

            services.Configure<RequestLocalizationOptions>(options => {
                var language = config.GetOrDefault("Settings:Language", string.Empty);
                var supportedCultures = new[]
                {
                    new CultureInfo("en-GB"),
                    new CultureInfo("fr-FR"),
                    new CultureInfo("de-DE"),
                    new CultureInfo("nl-NL"),
                    new CultureInfo("es-ES"),
                };

                CurrentCulture = GetDefaultCulture(supportedCultures, language);
                if (!string.IsNullOrWhiteSpace(language) && !string.Equals("en-GB", CurrentCulture, StringComparison.OrdinalIgnoreCase))
                {
                    supportedCultures = supportedCultures?.Where(x => CultureMatches(x, CurrentCulture))?.ToArray();
                }

                options.DefaultRequestCulture = new RequestCulture(CurrentCulture);
                options.SupportedCultures = supportedCultures;
                options.SupportedUICultures = supportedCultures;
            });
        }

        private static string GetDefaultCulture(CultureInfo[] supported, string key)
        {
            try
            {
                foreach (var culture in supported)
                {
                    if (CultureMatches(culture, key))
                    {
                        return culture.Name;
                    }
                }
            }
            catch
            {
            }

            return "en-GB";
        }

        private static bool CultureMatches(CultureInfo culture, string key)
        {
            return string.Equals(culture.Name, key, StringComparison.OrdinalIgnoreCase) || string.Equals(culture.ThreeLetterISOLanguageName, key, StringComparison.OrdinalIgnoreCase) || string.Equals(culture.TwoLetterISOLanguageName, key, StringComparison.OrdinalIgnoreCase);
        }
    }
}

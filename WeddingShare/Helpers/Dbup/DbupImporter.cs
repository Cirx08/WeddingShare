using System.Collections;
using System.Reflection;
using WeddingShare.Constants;
using WeddingShare.Helpers.Database;
using WeddingShare.Models.Database;

namespace WeddingShare.Helpers.Dbup
{
    public class DbupImporter(IConfigHelper config, IDatabaseHelper database, ILogger<DbupImporter> logger)
    {
        public async Task ImportSettings()
        {
            try
            {
                var galleries = (await database.GetAllGalleries())?.Where(x => !x.Identifier.Equals("All", StringComparison.OrdinalIgnoreCase));

                var settings = await database.GetAllSettings();
                if (settings == null || !settings.Any(setting => setting.Id.StartsWith(Settings.Basic.BaseKey, StringComparison.OrdinalIgnoreCase)))
                {
                    var systemKeys = GetAllKeys();
                    foreach (var key in systemKeys)
                    {
                        try
                        {
                            if (settings == null || !settings.Any(setting => setting.Id.Equals(key, StringComparison.OrdinalIgnoreCase)))
                            {
                                var configVal = config.Get(key);
                                if (!string.IsNullOrWhiteSpace(configVal))
                                {
                                    await database.AddSetting(new SettingModel()
                                    {
                                        Id = key,
                                        Value = configVal
                                    });
                                }
                            }
                        }
                        catch { }
                    }

                    if (galleries != null && galleries.Any())
                    {
                        var galleryKeys = GetKeys<Constants.Settings.Gallery>();
                        foreach (var gallery in galleries)
                        {
                            if (!string.IsNullOrWhiteSpace(gallery?.Name))
                            {
                                foreach (var key in galleryKeys)
                                {
                                    try
                                    {
                                        var galleryOverride = config.GetEnvironmentVariable(key, gallery.Name);
                                        if (!string.IsNullOrWhiteSpace(galleryOverride))
                                        {
                                            await database.AddSetting(new SettingModel()
                                            {
                                                Id = key,
                                                Value = galleryOverride
                                            }, gallery.Id);
                                        }
                                    }
                                    catch { }
                                }
                            }
                        }
                    }
                }

                // Protect any galleries without a secret key by forcing a new one
                if (galleries != null && galleries.Any())
                {
                    var hardenGallerySecurity = config.GetOrDefault(Constants.Security.HardenGallerySecurity, false);
                    if (hardenGallerySecurity)
                    {
                        foreach (var gallery in galleries.Where(gallery => string.IsNullOrWhiteSpace(gallery.SecretKey)))
                        {
                            try
                            {
                                if (gallery.Identifier.Equals("default", StringComparison.OrdinalIgnoreCase))
                                {
                                    gallery.SecretKey = config.GetOrDefault(Settings.Basic.DefaultGallerySecretKey, PasswordHelper.GenerateGallerySecretKey());
                                }
                                else
                                {
                                    gallery.SecretKey = PasswordHelper.GenerateGallerySecretKey();
                                }
                            
                                await database.EditGallery(gallery);
                            }
                            catch { }
                        }
                    }
                }
            }
            catch (Exception ex) 
            {
                logger.LogError($"Failed to import settings at startup - {ex?.Message}", ex);
            }
        }

        private IEnumerable<string> GetAllKeys()
        {
            var keys = new List<string>();

            try
            {
                keys.AddRange(GetKeys<Constants.BackgroundServices>());
                keys.AddRange(GetKeys<Constants.Notifications>());
                keys.AddRange(GetKeys<Constants.Security>());
                keys.AddRange(GetKeys<Constants.Settings>());
            }
            catch { }

            return keys.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct();
        }

        private IEnumerable<string> GetKeys<T>(bool includeNesteted = true)
        {
            var keys = new List<string>();

            try
            {
                var obj = Activator.CreateInstance<T>();
                foreach (var val in GetConstants(typeof(T), includeNesteted))
                {
                    keys.Add((string)(val.GetValue(obj) ?? string.Empty));
                }
            }
            catch { }

            return keys.Where(x => !string.IsNullOrWhiteSpace(x));
        }

        private FieldInfo[] GetConstants(Type type, bool includeNesteted)
        {
            var constants = new ArrayList();

            try
            {
                if (includeNesteted)
                { 
                    var classInfos = type.GetNestedTypes(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                    foreach (var ci in classInfos)
                    {
                        var consts = GetConstants(ci, includeNesteted);
                        if (consts != null && consts.Length > 0)
                        {
                            constants.AddRange(consts);
                        }
                    }
                }

                var fieldInfos = type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                foreach (var fi in fieldInfos)
                {
                    if (fi.IsLiteral && !fi.IsInitOnly)
                    {
                        constants.Add(fi);
                    }
                }
            }
            catch { }

            return (FieldInfo[])constants.ToArray(typeof(FieldInfo));
        }
    }
}
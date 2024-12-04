﻿namespace WeddingShare.Helpers
{
    public interface IConfigHelper
    {
        string? GetEnvironmentVariable(string key);
        string? GetConfigValue(string section, string key);
        string? Get(string section, string key);
        string GetOrDefault(string section, string key, string defaultValue);
        int GetOrDefault(string section, string key, int defaultValue);
        long GetOrDefault(string section, string key, long defaultValue);
        decimal GetOrDefault(string section, string key, decimal defaultValue);
        double GetOrDefault(string section, string key, double defaultValue);
        bool GetOrDefault(string section, string key, bool defaultValue);
        DateTime? GetOrDefault(string section, string key, DateTime? defaultValue);
    }

    public class ConfigHelper : IConfigHelper
    {
        private readonly IEnvironmentWrapper _environment;
        private readonly IConfiguration _configuration;
        private readonly ILogger _logger;

        public ConfigHelper(IEnvironmentWrapper environment, IConfiguration config, ILogger<ConfigHelper> logger)
        {
            _environment = environment;
            _configuration = config;
            _logger = logger;
        }

        public string? GetEnvironmentVariable(string key)
        {
            try
            {
                var value = _environment.GetEnvironmentVariable(key.Replace(":", "_").ToUpper());
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to get environment variable '{key}'");
            }

            return null;
        }

        public string? GetConfigValue(string section, string key)
        {
            try
            {
                var configKey = !string.IsNullOrWhiteSpace(section) ? $"{section}:{key}" : key;
                var value = _configuration.GetValue<string>(configKey);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to get config value '{key}'");
            }

            return null;
        }

        public string? Get(string section, string key)
        {
            try
            {
                var value = !IsProtectedVariable($"{section}_{key}") ? this.GetEnvironmentVariable(key) : string.Empty;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }

                value = this.GetConfigValue(section, key);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to find key '{key}' in either environment variables or appsettings");
            }

            return null;
        }

        public string GetOrDefault(string section, string key, string defaultValue)
        {
            try
            {
                var value = this.Get(section, key);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
            catch { }

            return defaultValue;
        }

        public int GetOrDefault(string section, string key, int defaultValue)
        {
            try
            {
                var value = this.GetOrDefault(section, key, string.Empty);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return Convert.ToInt32(value);
                }
            }
            catch { }

            return defaultValue;
        }

        public long GetOrDefault(string section, string key, long defaultValue)
        {
            try
            {
                var value = this.GetOrDefault(section, key, string.Empty);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return Convert.ToInt64(value);
                }
            }
            catch { }

            return defaultValue;
        }

        public decimal GetOrDefault(string section, string key, decimal defaultValue)
        {
            try
            {
                var value = this.GetOrDefault(section, key, string.Empty);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return Convert.ToDecimal(value);
                }
            }
            catch { }

            return defaultValue;
        }

        public double GetOrDefault(string section, string key, double defaultValue)
        {
            try
            {
                var value = this.GetOrDefault(section, key, string.Empty);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return Convert.ToDouble(value);
                }
            }
            catch { }

            return defaultValue;
        }

        public bool GetOrDefault(string section, string key, bool defaultValue)
        {
            try
            {
                var value = this.GetOrDefault(section, key, string.Empty);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return Convert.ToBoolean(value);
                }
            }
            catch { }

            return defaultValue;
        }

        public DateTime? GetOrDefault(string section, string key, DateTime? defaultValue)
        {
            try
            {
                var value = this.GetOrDefault(section, key, string.Empty);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return Convert.ToDateTime(value);
                }
            }
            catch { }

            return defaultValue;
        }

        private bool IsProtectedVariable(string key)
        {
            switch (key.Replace(":", "_").Trim('_').ToUpper())
            {
                case "RELEASE_VERSION":
                    return true;
                default:
                    return false;
            }
        }
    }
}
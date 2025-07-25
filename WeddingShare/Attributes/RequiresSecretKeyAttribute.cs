﻿using System.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using WeddingShare.Constants;
using WeddingShare.Helpers;
using WeddingShare.Helpers.Database;

namespace WeddingShare.Attributes
{
    public class RequiresSecretKeyAttribute : ActionFilterAttribute
    {
        public override async void OnActionExecuting(ActionExecutingContext filterContext)
        {
            try
            {
                var request = filterContext.HttpContext.Request;
                var databaseHelper = filterContext.HttpContext.RequestServices.GetService<IDatabaseHelper>();

                var galleryName = (request.Query.ContainsKey("id") && !string.IsNullOrWhiteSpace(request.Query["id"])) ? request.Query["id"].ToString().ToLower() : "default";
                var galleryId = (databaseHelper?.GetGalleryId(galleryName)?.Result) ?? 1;

                var gallery = databaseHelper?.GetGallery(galleryId).Result;
                if (gallery != null)
                { 
                    var encryptionHelper = filterContext.HttpContext.RequestServices.GetService<IEncryptionHelper>();
                    if (encryptionHelper != null)
                    { 
                        var key = request.Query.ContainsKey("key") ? request.Query["key"].ToString() : string.Empty;

                        var isEncrypted = request.Query.ContainsKey("enc") ? bool.Parse(request.Query["enc"].ToString().ToLower()) : false;
                        if (!isEncrypted && !string.IsNullOrWhiteSpace(key) && encryptionHelper.IsEncryptionEnabled())
                        {
                            var queryString = HttpUtility.ParseQueryString(request.QueryString.ToString());
                            queryString.Set("enc", "true");
                            queryString.Set("key", encryptionHelper.Encrypt(key));

                            filterContext.Result = new RedirectResult($"/Gallery?{queryString.ToString()}");
                        }
                        else
                        {
                            var settingsHelper = filterContext.HttpContext.RequestServices.GetService<ISettingsHelper>();
                            if (settingsHelper != null)
                            {
                                var secretKey = settingsHelper.GetOrDefault(Settings.Gallery.SecretKey, string.Empty, galleryId).Result ?? string.Empty;
                                if (!string.IsNullOrWhiteSpace(secretKey))
                                {
                                    secretKey = encryptionHelper.IsEncryptionEnabled() ? encryptionHelper.Encrypt(secretKey) : secretKey;
                                    if (!string.IsNullOrWhiteSpace(secretKey) && !string.Equals(secretKey, key))
                                    {
                                        var logger = filterContext.HttpContext.RequestServices.GetService<ILogger<RequiresSecretKeyAttribute>>();
                                        if (logger != null)
                                        {
                                            logger.LogWarning($"A request was made to an endpoint with an invalid secure key");
                                        }

                                        filterContext.Result = new RedirectToActionResult("Index", "Error", new { Reason = ErrorCode.InvalidSecretKey }, false);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                var logger = filterContext.HttpContext.RequestServices.GetService<ILogger<RequiresSecretKeyAttribute>>();
                if (logger != null)
                {
                    logger.LogError(ex, $"Failed to validate secure key - {ex?.Message}");
                }
            }
        }
    }
}
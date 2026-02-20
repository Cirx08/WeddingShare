using System.IO.Compression;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using TwoFactorAuthNet;
using WeddingShare.Attributes;
using WeddingShare.Constants;
using WeddingShare.Enums;
using WeddingShare.Extensions;
using WeddingShare.Helpers;
using WeddingShare.Helpers.Database;
using WeddingShare.Helpers.Notifications;
using WeddingShare.Models;
using WeddingShare.Models.Database;
using WeddingShare.Resources.Templates.Email;
using WeddingShare.Views.Account;

namespace WeddingShare.Controllers
{
    [Authorize]
    public class AccountController : BaseController
    {
        private readonly IWebHostEnvironment _hostingEnvironment;
        private readonly ISettingsHelper _settings;
        private readonly IDatabaseHelper _database;
        private readonly IDeviceDetector _deviceDetector;
        private readonly IFileHelper _fileHelper;
        private readonly IEncryptionHelper _encryption;
        private readonly INotificationHelper _notificationHelper;
        private readonly ISmtpClientWrapper _smtpClientWrapper;
        private readonly Helpers.IUrlHelper _url;
        private readonly IAuditHelper _audit;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger _logger;
        private readonly IStringLocalizer<Lang.Translations> _localizer;

        private readonly string TempDirectory;
        private readonly string UploadsDirectory;
        private readonly string ThumbnailsDirectory;
        private readonly string CustomResourcesDirectory;

        private const string DefaultUserGalleryName = "Default";

        public AccountController(IWebHostEnvironment hostingEnvironment, ISettingsHelper settings, IDatabaseHelper database, IDeviceDetector deviceDetector, IFileHelper fileHelper, IEncryptionHelper encryption, INotificationHelper notificationHelper, ISmtpClientWrapper smtpClientWrapper, Helpers.IUrlHelper url, IAuditHelper audit, ILoggerFactory loggerFactory, IStringLocalizer<Lang.Translations> localizer)
            : base()
        {
            _hostingEnvironment = hostingEnvironment;
            _settings = settings;
            _database = database;
            _deviceDetector = deviceDetector;
            _fileHelper = fileHelper;
            _encryption = encryption;
            _notificationHelper = notificationHelper;
            _smtpClientWrapper = smtpClientWrapper;
            _url = url;
            _audit = audit;
            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger<AccountController>();
            _localizer = localizer;

            TempDirectory = Path.Combine(_hostingEnvironment.WebRootPath, Directories.TempFiles);
            UploadsDirectory = Path.Combine(_hostingEnvironment.WebRootPath, Directories.Uploads);
            ThumbnailsDirectory = Path.Combine(_hostingEnvironment.WebRootPath, Directories.Thumbnails);
            CustomResourcesDirectory = Path.Combine(_hostingEnvironment.WebRootPath, Directories.CustomResources);
        }

        [AllowAnonymous]
        [HttpGet]
        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Login()
        {
            if (User?.Identity != null && User.Identity.IsAuthenticated)
            {
                return RedirectToAction("Index", "Account");
            }

            return View();
        }

        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        [HttpPost]
        public async Task<IActionResult> Login(LoginModel model)
        {
            try
            {
                model.Username = model.Username.Trim();

                var user = await _database.GetUserByUsername(model.Username);
                if (user != null)
                {
                    if (user.State == AccountState.PendingActivation)
                    {
                        return Json(new LoginResponse(true)
                        {
                            PendingActivation = true
                        });
                    }
                    else if (user.State == AccountState.Active && !user.IsLockedOut)
                    {
                        if (await _database.ValidateCredentials(user.Username, _encryption.Encrypt(model.Password, user.Username.ToLower())))
                        {
                            if (user.FailedLogins > 0)
                            {
                                await _database.ResetLockoutCount(user.Id);
                            }

                            var mfaSet = !string.IsNullOrEmpty(user.MultiFactorToken);
                            HttpContext.Session.SetString(SessionKey.MultiFactorTokenSet, mfaSet.ToString().ToLower());

                            if (mfaSet)
                            {
                                return Json(new LoginResponse(true)
                                {
                                    MFAEnabled = true
                                });
                            }
                            else
                            {
                                await _audit.LogAction(user?.Username, _localizer["Audit_UserLoggedIn"].Value);

                                return Json(new LoginResponse(await this.SetUserClaims(this.HttpContext, user)));
                            }
                        }
                        else
                        {
                            await this.FailedLoginDetected(model, user);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"{_localizer["Login_Failed"].Value} - {ex?.Message}");
            }

            return Json(new LoginResponse(false));
        }

        [AllowAnonymous]
        [HttpGet]
        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Register()
        {
            if (User?.Identity != null && User.Identity.IsAuthenticated)
            {
                return RedirectToAction("Index", "Account");
            }

            return View();
        }

        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        [HttpPost]
        public async Task<IActionResult> Register(RegisterModel model)
        {
            if (await _settings.GetOrDefault(Settings.Account.Registration.Enabled, true))
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(model?.Username) || model.Username.Length < 5 || model.Username.Length > 50)
                    {
                        return Json(new { success = false, message = _localizer["Registration_Invalid_Username"].Value });
                    }
                    else if (string.IsNullOrWhiteSpace(model?.EmailAddress) || !EmailValidationHelper.IsValid(model.EmailAddress))
                    {
                        return Json(new { success = false, message = _localizer["Registration_Invalid_Email"].Value });
                    }
                    else if (string.IsNullOrWhiteSpace(model?.Password) || !PasswordHelper.IsValid(model.Password))
                    {
                        return Json(new { success = false, message = _localizer["Registration_Invalid_Password"].Value });
                    }
                    else if (PasswordHelper.IsWeak(model.Password))
                    {
                        return Json(new { success = false, message = _localizer["Registration_Weak_Password"].Value });
                    }
                    else if (string.IsNullOrWhiteSpace(model?.ConfirmPassword) || !model.ConfirmPassword.Equals(model.Password))
                    {
                        return Json(new { success = false, message = _localizer["Registration_Confirm_Password_Missmatch"].Value });
                    }
                    else if (await _database.GetUserByUsername(model.Username) != null)
                    {
                        return Json(new { success = false, message = _localizer["Registration_Username_Taken"].Value });
                    }
                    else if (await _database.GetUserByEmail(model.EmailAddress) != null)
                    {
                        return Json(new { success = false, message = _localizer["Registration_Email_Taken"].Value });
                    }
                    else
                    {
                        var requireEmailValidation = await _settings.GetOrDefault(Notifications.Smtp.Enabled, false)
                            && await _settings.GetOrDefault(Settings.Account.Registration.RequireEmailValidation, true);

                        var user = await _database.AddUser(new UserModel()
                        {
                            Username = model.Username.Trim().ToLower(),
                            Email = model.EmailAddress.Trim().ToLower(),
                            Password = _encryption.Encrypt(model.Password, model.Username.ToLower()),
                            State = requireEmailValidation ? AccountState.PendingActivation : AccountState.Active,
                            Level = UserLevel.Paid // TODO - Update to Free once payment logic is completed
                        });

                        if (user?.Id != null && user.Id > 0 && !string.IsNullOrWhiteSpace(user.Email))
                        {
                            try
                            {
                                var emailHelper = new EmailHelper(_settings, _smtpClientWrapper, _loggerFactory.CreateLogger<EmailHelper>(), _localizer);
                                if (requireEmailValidation)
                                {
                                    await emailHelper.SendTo(user.Email, _localizer["Registration_Success_Title"].Value, new BasicEmail()
                                        {
                                            Title = _localizer["Registration_Success_Title"].Value,
                                            Message = _localizer["Registration_Success_Verification"].Value,
                                            Link = new BasicEmailLink()
                                            {
                                                Heading = _localizer["Verify"].Value,
                                                Value = _url.GenerateFullUrl(HttpContext?.Request, "/Account/VerifyEmail", new List<KeyValuePair<string, string>>
                                                {
                                                    new KeyValuePair<string, string>("data", EncodingHelper.Base64Encode(JsonSerializer.Serialize(new EmailVerificationModel()
                                                    {
                                                        Username = user.Username,
                                                        Validator = await _database.SetUserSecret(user.Id, PasswordHelper.GenerateSecretCode())
                                                    })))
                                                })
                                            }
                                        });
                                }
                                else
                                {
                                    await CreateDefaultUserGallery(user);

                                    await emailHelper.SendTo(model.EmailAddress, _localizer["Registration_Success_Title"].Value, new BasicEmail()
                                    {
                                        Title = _localizer["Registration_Success_Title"].Value,
                                        Message = _localizer["Registration_Success_Message"].Value,
                                        Link = new BasicEmailLink()
                                        {
                                            Heading = _localizer["Visit"].Value,
                                            Value = _url.GenerateBaseUrl(HttpContext?.Request, "/Account/Login")
                                        }
                                    });
                                }

                                await _audit.LogAction(user.Username, $"{_localizer["Audit_Account_Registered"].Value}");
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, $"{_localizer["Registration_Email_Send_Failed"].Value} Email: '{model.EmailAddress}'");
                            }

                            return Json(new { success = true, validation = requireEmailValidation });
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"{_localizer["Registration_Failed"].Value} - {ex?.Message}");
                }
            }

            return Json(new { success = false });
        }

        [AllowAnonymous]
        [HttpGet]
        public async Task<IActionResult> VerifyEmail(string data)
        {
            if (!string.IsNullOrWhiteSpace(data) && await _settings.GetOrDefault(Settings.Account.Registration.Enabled, true))
            {
                try
                {
                    var json = EncodingHelper.Base64Decode(HttpUtility.UrlDecode(data));
                    var model = JsonSerializer.Deserialize<EmailVerificationModel>(json);
                    if (!string.IsNullOrWhiteSpace(model?.Username) && !string.IsNullOrWhiteSpace(model?.Validator))
                    {
                        var user = await _database.GetUserByUsername(model.Username);
                        if (user != null)
                        { 
                            if (await _database.VerifyUserSecret(user.Id, model.Validator))
                            {
                                user.State = AccountState.Active;

                                await _database.SetUserSecret(user.Id, PasswordHelper.GenerateSecretCode());
                                await CreateDefaultUserGallery(user);

                                await _audit.LogAction(user.Username, $"{_localizer["Audit_Email_Verified"].Value}");

                                if ((await _database.EditUser(user))?.State == user.State && await this.SetUserClaims(this.HttpContext, user))
                                {
                                    return new RedirectToActionResult("Index", "Account", null, false);
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"{_localizer["Registration_Invalid_Verification_Link"].Value} - {ex?.Message}");
                }
            }

            return new RedirectToActionResult("Index", "Error", new { Reason = ErrorCode.InvalidVerificationLink }, false);
        }

        [AllowAnonymous]
        [HttpGet]
        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult ForgotPassword()
        {
            return View();
        }

        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        [HttpPost]
        public async Task<IActionResult> ForgotPassword(string emailAddress)
        {
            if (!string.IsNullOrWhiteSpace(emailAddress))
            {
                try
                {
                    var user = await _database.GetUserByEmail(emailAddress);
                    if (user != null && !string.IsNullOrWhiteSpace(user?.Email))
                    {
                        await new EmailHelper(_settings, _smtpClientWrapper, _loggerFactory.CreateLogger<EmailHelper>(), _localizer)
                            .SendTo(user.Email, _localizer["Password_Reset_Requested_Title"].Value, new BasicEmail()
                            {
                                Title = _localizer["PasswordReset"].Value,
                                Message = _localizer["Password_Reset_Requested_Message"].Value,
                                Link = new BasicEmailLink()
                                {
                                    Heading = _localizer["Visit"].Value,
                                    Value = _url.GenerateFullUrl(HttpContext?.Request, "/Account/ResetPassword", new List<KeyValuePair<string, string>>
                                    {
                                        new KeyValuePair<string, string>("data", EncodingHelper.Base64Encode(JsonSerializer.Serialize(new EmailVerificationModel()
                                        {
                                            Username = user.Username,
                                            Validator = await _database.SetUserSecret(user.Id, PasswordHelper.GenerateSecretCode())
                                        })))
                                    })
                                }
                            });

                        await _audit.LogAction(user.Username, $"{_localizer["Audit_Forgot_Password"].Value}");

                        return Json(new { success = true });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"{_localizer["ForgotPassword_Failed"].Value}. EmailAddress: '{emailAddress}' - {ex?.Message}");
                }
            }

            return Json(new { success = false });
        }

        [AllowAnonymous]
        [HttpGet]
        public async Task<IActionResult> ResetPassword(string data)
        {
            if (!string.IsNullOrWhiteSpace(data) && await _settings.GetOrDefault(Settings.Account.Registration.Enabled, true))
            {
                try
                {
                    var json = EncodingHelper.Base64Decode(HttpUtility.UrlDecode(data));
                    var model = JsonSerializer.Deserialize<EmailVerificationModel>(json);
                    if (!string.IsNullOrWhiteSpace(model?.Username) && !string.IsNullOrWhiteSpace(model?.Validator))
                    {
                        var user = await _database.GetUserByUsername(model.Username);
                        if (user != null && !string.IsNullOrWhiteSpace(user.Email))
                        {
                            if (await _database.VerifyUserSecret(user.Id, model.Validator))
                            {
                                return View(new ResetPasswordViewModel()
                                {
                                    Data = data
                                });
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"{_localizer["ResetPassword_Invalid_Reset_Link"].Value} - {ex?.Message}");
                }
            }

            return new RedirectToActionResult("Index", "Error", new { Reason = ErrorCode.InvalidPasswordResetLink }, false);
        }

        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        [HttpPost]
        public async Task<IActionResult> ResetPassword(ResetPasswordModel model)
        {
            if (await _settings.GetOrDefault(Settings.Account.Registration.Enabled, true) && !string.IsNullOrWhiteSpace(model?.Data))
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(model?.Password) || !PasswordHelper.IsValid(model.Password))
                    {
                        return Json(new { success = false, message = _localizer["Registration_Invalid_Password"].Value });
                    }
                    else if (PasswordHelper.IsWeak(model.Password))
                    {
                        return Json(new { success = false, message = _localizer["Registration_Weak_Password"].Value });
                    }
                    else if (string.IsNullOrWhiteSpace(model?.ConfirmPassword) || !model.ConfirmPassword.Equals(model.Password))
                    {
                        return Json(new { success = false, message = _localizer["Registration_Confirm_Password_Missmatch"].Value });
                    }
                    else
                    {
                        var json = EncodingHelper.Base64Decode(HttpUtility.UrlDecode(model.Data));
                        var data = JsonSerializer.Deserialize<EmailVerificationModel>(json);
                        if (!string.IsNullOrWhiteSpace(data?.Username) && !string.IsNullOrWhiteSpace(data?.Validator))
                        {
                            var user = await _database.GetUserByUsername(data.Username);
                            if (user != null && !string.IsNullOrWhiteSpace(user.Email))
                            {
                                if (await _database.VerifyUserSecret(user.Id, data.Validator))
                                {
                                    user.Password = _encryption.Encrypt(model.Password, user.Username.ToLower());

                                    if (await _database.ChangePassword(user))
                                    {
                                        await _database.SetUserSecret(user.Id, PasswordHelper.GenerateSecretCode());

                                        await new EmailHelper(_settings, _smtpClientWrapper, _loggerFactory.CreateLogger<EmailHelper>(), _localizer)
                                            .SendTo(user.Email, _localizer["Password_Reset_Changed_Title"].Value, new BasicEmail() 
                                            {
                                                Title = _localizer["PasswordReset"].Value,
                                                Message = _localizer["Password_Reset_Changed_Message"].Value,
                                                Link = new BasicEmailLink()
                                                {
                                                    Heading = _localizer["Visit"].Value,
                                                    Value = _url.GenerateBaseUrl(HttpContext?.Request, "/Account/Login")
                                                }
                                            });

                                        await _audit.LogAction(user.Username, $"{_localizer["Audit_Password_Reset"].Value}");

                                        return Json(new { success = true, username = user.Username, mfa = !string.IsNullOrWhiteSpace(user.MultiFactorToken) });
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"{_localizer["PasswordReset_Failed"].Value} - {ex?.Message}");
                }
            }

            return Json(new { success = false });
        }

        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        [HttpPost]
        public async Task<IActionResult> ValidateMultifactorAuth(LoginModel model)
        {
            if (!string.IsNullOrWhiteSpace(model?.Code))
            { 
                try
                {
                    model.Username = model.Username.Trim();

                    var user = await _database.GetUserByUsername(model.Username);
                    if (user != null && user.State == AccountState.Active && !user.IsLockedOut)
                    {
                        if (await _database.ValidateCredentials(user.Username, _encryption.Encrypt(model.Password, user.Username.ToLower())))
                        {
                            if (user.FailedLogins > 0)
                            {
                                await _audit.LogAction(user?.Username, _localizer["Audit_FailedLoginAttemptReset"].Value);
                                await _database.ResetLockoutCount(user.Id);
                            }

                            var mfaSet = !string.IsNullOrWhiteSpace(user.MultiFactorToken);
                            HttpContext.Session.SetString(SessionKey.MultiFactorTokenSet, (!string.IsNullOrEmpty(user.MultiFactorToken)).ToString().ToLower());

                            if (mfaSet)
                            {
                                var tfa = new TwoFactorAuth(await _settings.GetOrDefault(Settings.Basic.Title, "WeddingShare"));
                                if (tfa.VerifyCode(user.MultiFactorToken, model.Code))
                                {
                                    await _audit.LogAction(user?.Username, _localizer["Audit_MultiFactorPassed"].Value);
                                    return Json(new { success = await this.SetUserClaims(this.HttpContext, user) });
                                }
                            }
                            else
                            {
                                await _audit.LogAction(user?.Username, _localizer["Audit_UserLoggedIn"].Value);
                                return Json(new { success = await this.SetUserClaims(this.HttpContext, user) });
                            }
                        }
                        else
                        {
                            await this.FailedLoginDetected(model, user);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"{_localizer["Login_Failed"].Value} - {ex?.Message}");
                }
            }

            return Json(new { success = false });
        }

        [Authorize]
        [HttpGet]
        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public async Task<IActionResult> Logout()
        {
            await _audit.LogAction(User?.Identity?.Name, _localizer["Audit_LoggedOut"].Value);
            await this.HttpContext.SignOutAsync();
            return RedirectToAction("Index", "Account");
        }

        [HttpGet]
        public async Task<IActionResult> Index(AccountTabs? tab = null)
        {
            if (User?.Identity == null || !User.Identity.IsAuthenticated)
            { 
                return Redirect("/");
            }

            var model = new IndexModel()
            {
                ActiveTab = tab ?? (User?.Identity?.GetDefaultTab() ?? AccountTabs.Reviews)
            };

            var deviceType = HttpContext.Session.GetString(SessionKey.DeviceType);
            if (string.IsNullOrWhiteSpace(deviceType))
            {
                deviceType = (await _deviceDetector.ParseDeviceType(Request.Headers["User-Agent"].ToString())).ToString();
                HttpContext.Session.SetString(SessionKey.DeviceType, deviceType ?? "Desktop");
            }

            try
            {
                var user = await _database.GetUser(User.Identity.GetUserId());
                if (user != null)
                {
                    if (User?.Identity?.IsPrivilegedUser() ?? false)
                    {
                        if (model.ActiveTab == AccountTabs.Reviews)
                        {
                            model.PendingRequests = await GetPendingReviews();
                        }
                        else if (model.ActiveTab == AccountTabs.Galleries)
                        {
                            model.Galleries = (await _database.GetAllGalleries())?.Where(x => !x.Identifier.Equals("All", StringComparison.OrdinalIgnoreCase))?.ToList();
                            if (model.Galleries != null)
                            {
                                var all = await _database.GetAllGallery();
                                if (all != null)
                                {
                                    model.Galleries.Add(all);
                                }
                            }
                        }
                        else if (model.ActiveTab == AccountTabs.Users)
                        {
                            model.Users = await _database.GetAllUsers();
                        }
                        else if (model.ActiveTab == AccountTabs.Resources)
                        {
                            model.CustomResources = await _database.GetAllCustomResources();
                        }
                        else if (model.ActiveTab == AccountTabs.Settings)
                        {
                            model.Settings = (await _database.GetAllSettings())?.ToDictionary(x => x.Id.ToUpper(), x => x.Value ?? string.Empty);
                            model.CustomResources = await _database.GetAllCustomResources();
                        }
                        else if (model.ActiveTab == AccountTabs.Audit)
                        {
                            model.AuditLogs = await _database.GetAuditLogs();
                        }
                    }
                    else
                    {
                        if (model.ActiveTab == AccountTabs.Reviews)
                        {
                            model.PendingRequests = await GetPendingReviews(user.Id);
                        }
                        else if (model.ActiveTab == AccountTabs.Galleries)
                        {
                            model.Galleries = await _database.GetUserGalleries(user.Id);
                        }
                        else if (model.ActiveTab == AccountTabs.Users)
                        {
                            model.Users = new List<UserModel>() { user };
                        }
                        else if (model.ActiveTab == AccountTabs.Resources)
                        {
                            model.CustomResources = await _database.GetUserCustomResources(user.Id);
                        }
                        else if (model.ActiveTab == AccountTabs.Settings)
                        {
                            // Basic users do not have access to global site settings
                        }
                        else if (model.ActiveTab == AccountTabs.Audit)
                        {
                            model.AuditLogs = await _database.GetUserAuditLogs(user.Id);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"{_localizer["Pending_Uploads_Failed"].Value} - {ex?.Message}");
            }

            return View(model);
        }

        [HttpGet]
        [RequiresRole(GalleryPermission = GalleryPermissions.View)]
        public async Task<IActionResult> GalleriesList()
        {
            if (User?.Identity == null || !User.Identity.IsAuthenticated)
            {
                return Redirect("/");
            }

            List<GalleryModel>? result = null;

            try
            {
                var user = await _database.GetUser(User.Identity.GetUserId());
                if (user != null)
                {
                    if (User?.Identity?.IsPrivilegedUser() ?? false)
                    {
                        result = (await _database.GetAllGalleries())?.Where(x => !x.Identifier.Equals("All", StringComparison.OrdinalIgnoreCase))?.ToList();
                        if (result != null)
                        {
                            var all = await _database.GetAllGallery();
                            if (all != null)
                            {
                                result.Add(all);
                            }
                        }
                    }
                    else
                    {
                        result = await _database.GetUserGalleries(user.Id);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"{_localizer["Gallery_List_Failed"].Value} - {ex?.Message}");
            }

            return PartialView("~/Views/Account/Partials/GalleriesList.cshtml", result ?? new List<GalleryModel>());
        }

        [HttpGet]
        [RequiresRole(ReviewPermission = ReviewPermissions.View)]
        public async Task<IActionResult> PendingReviews()
        {
            var galleries = new List<PhotoGallery>();

            if (User?.Identity == null || !User.Identity.IsAuthenticated)
            {
                return Redirect("/");
            }

            try
            {
                var user = await _database.GetUser(User.Identity.GetUserId());
                if (user != null)
                {
                    if (User?.Identity?.IsPrivilegedUser() ?? false)
                    {
                        galleries = await GetPendingReviews();
                    }
                    else
                    {
                        galleries = await GetPendingReviews(user.Id);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"{_localizer["Pending_Uploads_Failed"].Value} - {ex?.Message}");
            }

            return PartialView("~/Views/Account/Partials/PendingReviews.cshtml", galleries);
        }

        [HttpGet]
        [RequiresRole(UserPermission = UserPermissions.View)]
        public async Task<IActionResult> UsersList()
        {
            if (User?.Identity == null || !User.Identity.IsAuthenticated)
            {
                return Redirect("/");
            }

            List<UserModel>? result = null;

            try
            {
                var user = await _database.GetUser(User.Identity.GetUserId());
                if (user != null)
                {
                    if (User?.Identity?.IsPrivilegedUser() ?? false)
                    {
                        result = await _database.GetAllUsers();
                    }
                    else 
                    {
                        result = new List<UserModel>() { user };
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"{_localizer["Users_List_Failed"].Value} - {ex?.Message}");
            }

            return PartialView("~/Views/Account/Partials/UsersList.cshtml", result ?? new List<UserModel>());
        }

        [HttpGet]
        [RequiresRole(CustomResourcePermission = CustomResourcePermissions.View)]
        public async Task<IActionResult> CustomResources()
        {
            if (User?.Identity == null || !User.Identity.IsAuthenticated)
            {
                return Redirect("/");
            }

            List<CustomResourceModel>? result = null;

            try
            {
                var user = await _database.GetUser(User.Identity.GetUserId());
                if (user != null)
                {
                    if (User?.Identity?.IsPrivilegedUser() ?? false)
                    {
                        result = await _database.GetAllCustomResources();
                    }
                    else
                    { 
                        result = await _database.GetUserCustomResources(user.Id);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"{_localizer["Custom_Resources_Failed"].Value} - {ex?.Message}");
            }

            return PartialView("~/Views/Account/Partials/CustomResources.cshtml", result ?? new List<CustomResourceModel>());
        }

        [HttpGet]
        [RequiresRole(SettingsPermission = SettingsPermissions.View)]
        public async Task<IActionResult> SettingsPartial()
        {
            if (User?.Identity == null || !User.Identity.IsAuthenticated)
            {
                return Redirect("/");
            }

            var model = new Views.Account.Partials.SettingsListModel();

            try
            {
                var user = await _database.GetUser(User.Identity.GetUserId());
                if (user != null)
                {
                    if (User?.Identity?.IsPrivilegedUser() ?? false)
                    {
                        model.Settings = (await _database.GetAllSettings())?.ToDictionary(x => x.Id.ToUpper(), x => x.Value ?? string.Empty);
                        model.CustomResources = await _database.GetAllCustomResources();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"{_localizer["Settings_Failed"].Value} - {ex?.Message}");
            }

            return PartialView("~/Views/Account/Partials/SettingsList.cshtml", model);
        }

        [HttpPost]
        [RequiresRole(AuditPermission = AuditPermissions.View)]
        public async Task<IActionResult> AuditList(string term = "", int limit = 100)
        {
            if (User?.Identity == null || !User.Identity.IsAuthenticated)
            {
                return Redirect("/");
            }

            IEnumerable<AuditLogModel>? result = null;

            try
            {
                var user = await _database.GetUser(User.Identity.GetUserId());
                if (user != null)
                {
                    limit = limit >= 5 ? limit : 5;

                    if (User?.Identity?.IsPrivilegedUser() ?? false)
                    {
                        result = await _database.GetAuditLogs(term, limit);
                    }
                    else
                    {
                        result = await _database.GetUserAuditLogs(user.Id, term, limit);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"{_localizer["Audit_List_Failed"].Value} - {ex?.Message}");
            }

            return PartialView("~/Views/Account/Partials/AuditList.cshtml", result ?? new List<AuditLogModel>());
        }

        [HttpGet]
        [RequiresRole(SettingsPermission = SettingsPermissions.Gallery_Update)]
        [Route("Account/Settings/{galleryId}")]
        public async Task<IActionResult> GallerySettingsPartial(int galleryId)
        {
            if (User?.Identity == null || !User.Identity.IsAuthenticated)
            {
                return Redirect("/");
            }

            var model = new Views.Account.Settings.GalleryModel();

            try
            {
                var gallery = await _database.GetGallery(galleryId);
                if (!string.IsNullOrWhiteSpace(gallery?.Name))
                {
                    model.Settings = (await _database.GetAllSettings(gallery.Id))?.Where(x => x.Id.StartsWith(Settings.Gallery.BaseKey, StringComparison.OrdinalIgnoreCase))?.ToDictionary(x => x.Id.ToUpper(), x => x.Value ?? string.Empty);
                    model.CustomResources = User.Identity.IsPrivilegedUser() ? await _database.GetAllCustomResources() : await _database.GetUserCustomResources(User.Identity.GetUserId());
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"{_localizer["Settings_Failed"].Value} - {ex?.Message}");
            }

            return PartialView("~/Views/Account/Settings/Gallery.cshtml", model);
        }

        [HttpPost]
        [RequiresRole(ReviewPermission = ReviewPermissions.View)]
        public async Task<IActionResult> ReviewPhoto(int id, ReviewAction action)
        {
            if (User?.Identity != null && User.Identity.IsAuthenticated)
            {
                try
                {
                    var review = await _database.GetPendingGalleryItem(id);
                    if (review != null)
                    {
                        var gallery = await _database.GetGallery(review.GalleryId);
                        if (gallery != null && User.Identity.CanEdit(ReviewPermissions.View, gallery.Owner))
                        { 
                            var galleryDir = Path.Combine(UploadsDirectory, gallery.Identifier);
                            var reviewFile = Path.Combine(galleryDir, "Pending", review.Title);
                            if (action == ReviewAction.APPROVED)
                            {
                                _fileHelper.MoveFileIfExists(reviewFile, Path.Combine(galleryDir, review.Title));

                                review.State = GalleryItemState.Approved;
                                await _database.EditGalleryItem(review);

                                await _audit.LogAction(User?.Identity?.Name, $"'{review.Title}' {_localizer["Audit_ItemApprovedInGallery"].Value} '{gallery.Identifier}'");
                            }
                            else if (action == ReviewAction.REJECTED)
                            {
                                var retain = await _settings.GetOrDefault(Settings.Gallery.RetainRejectedItems, false);
                                if (retain)
                                {
                                    var rejectedDir = Path.Combine(galleryDir, "Rejected");
                                    _fileHelper.CreateDirectoryIfNotExists(rejectedDir);
                                    _fileHelper.MoveFileIfExists(reviewFile, Path.Combine(rejectedDir, review.Title));
                                }
                                else
                                {
                                    _fileHelper.DeleteFileIfExists(reviewFile);
                                }

                                await _database.DeleteGalleryItem(review);

                                await _audit.LogAction(User?.Identity?.Name, $"'{review.Title}' {_localizer["Audit_ItemRejectedInGallery"].Value} '{gallery.Identifier}'");
                            }
                            else if (action == ReviewAction.UNKNOWN)
                            {
                                throw new Exception(_localizer["Unknown_Review_Action"].Value);
                            }

                            return Json(new { success = true, action });
                        }
                    }
                    else
                    {
                        return Json(new { success = false, message = _localizer["Failed_Finding_File"].Value });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"{_localizer["Failed_Reviewing_Media"].Value} - {ex?.Message}");
                }
            }

            return Json(new { success = false });
        }

        [HttpPost]
        [RequiresRole(ReviewPermission = ReviewPermissions.View)]
        public async Task<IActionResult> BulkReview(ReviewAction action)
        {
            if (User?.Identity != null && User.Identity.IsAuthenticated)
            {
                try
                {
                    var items = await _database.GetPendingGalleryItems();
                    if (items != null && items.Any())
                    {
                        foreach (var galleryGroup in items.GroupBy(x => x.GalleryId))
                        {
                            var gallery = await _database.GetGallery(galleryGroup.Key);
                            if (gallery != null && User.Identity.CanEdit(ReviewPermissions.View, gallery.Owner))
                            {
                                foreach (var review in galleryGroup)
                                {
                                    var galleryDir = Path.Combine(UploadsDirectory, gallery.Identifier);
                                    var reviewFile = Path.Combine(galleryDir, "Pending", review.Title);
                                    if (action == ReviewAction.APPROVED)
                                    {
                                        _fileHelper.MoveFileIfExists(reviewFile, Path.Combine(galleryDir, review.Title));

                                        review.State = GalleryItemState.Approved;
                                        await _database.EditGalleryItem(review);

                                        await _audit.LogAction(User?.Identity?.Name, _localizer["Audit_BulkApproveReviews"].Value);
                                    }
                                    else if (action == ReviewAction.REJECTED)
                                    {
                                        var retain = await _settings.GetOrDefault(Settings.Gallery.RetainRejectedItems, false);
                                        if (retain)
                                        {
                                            var rejectedDir = Path.Combine(galleryDir, "Rejected");
                                            _fileHelper.CreateDirectoryIfNotExists(rejectedDir);
                                            _fileHelper.MoveFileIfExists(reviewFile, Path.Combine(rejectedDir, review.Title));
                                        }
                                        else
                                        {
                                            _fileHelper.DeleteFileIfExists(reviewFile);
                                        }

                                        await _database.DeleteGalleryItem(review);

                                        await _audit.LogAction(User?.Identity?.Name, _localizer["Audit_BulkRejectReviews"].Value);
                                    }
                                    else if (action == ReviewAction.UNKNOWN)
                                    {
                                        throw new Exception(_localizer["Unknown_Review_Action"].Value);
                                    }
                                }
                            }
                        }
                    }
                     
                    return Json(new { success = true, action });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"{_localizer["Failed_Reviewing_Media"].Value} - {ex?.Message}");
                }
            }

            return Json(new { success = false });
        }

        [HttpPost]
        [RequiresRole(GalleryPermission = GalleryPermissions.Create)]
        public async Task<IActionResult> AddGallery(GalleryModel model)
        {
            if (User?.Identity != null && User.Identity.IsAuthenticated)
            {
                if (!string.IsNullOrWhiteSpace(model?.Name))
                {
                    try
                    {
                        var userId = User.Identity.GetUserId();
                        var userGalleries = await _database.GetUserGalleries(userId);

                        var alreadyExists = userGalleries.Any(x => x.Name.Equals(model.Name, StringComparison.OrdinalIgnoreCase)) || ((await _database.GetGalleryId(model.Identifier)) != null);
                        if (!alreadyExists)
                        {
                            if (userGalleries.Count() < User.Identity.GetGalleryLimit() && await _database.GetGalleryCount() < await _settings.GetOrDefault(Settings.Basic.MaxGalleryCount, 1000000))
                            {
                                model.Owner = userId;

                                var gallery = await _database.AddGallery(model);
                                if (gallery != null)
                                {
                                    await _audit.LogAction(User?.Identity?.Name, $"{_localizer["Audit_CreatedGallery"].Value} '{model?.Name}'");

                                    return Json(new { success = string.Equals(model?.Name, gallery?.Name, StringComparison.OrdinalIgnoreCase) });
                                }
                                else
                                {
                                    return Json(new { success = false, message = _localizer["Failed_Add_Gallery"].Value });
                                }
                            }
                            else
                            {
                                return Json(new { success = false, message = _localizer["Gallery_Limit_Reached"].Value });
                            }
                        }
                        else
                        { 
                            return Json(new { success = false, message = _localizer["Gallery_Name_Already_Exists"].Value });
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"{_localizer["Failed_Add_Gallery"].Value} - {ex?.Message}");
                    }
                }
                else
                { 
                    return Json(new { success = false, message = _localizer["Name_Cannot_Be_Blank"].Value });
                }
            }

            return Json(new { success = false });
        }

        [HttpPut]
        [RequiresRole(GalleryPermission = GalleryPermissions.Update)]
        public async Task<IActionResult> EditGallery(GalleryModel model)
        {
            if (User?.Identity != null && User.Identity.IsAuthenticated)
            {
                if (!string.IsNullOrWhiteSpace(model?.Name))
                {
                    try
                    {
                        var check = await _database.GetGallery(model.Id);
                        if (check == null || model.Id == check.Id)
                        {
                            var gallery = await _database.GetGallery(model.Id);
                            if (gallery != null && User.Identity.CanEdit(GalleryPermissions.Update, gallery.Owner))
                            {
                                gallery.Name = model.Name;
                                gallery.SecretKey = model.SecretKey;

                                gallery = await _database.EditGallery(gallery);
                                if (gallery != null)
                                {
                                    await _audit.LogAction(User?.Identity?.Name, $"{_localizer["Audit_UpdatedGallery"].Value} '{model?.Name}'");
                                
                                    return Json(new { success = string.Equals(model?.Name, gallery?.Name, StringComparison.OrdinalIgnoreCase) });
                                }
                                else
                                {
                                    return Json(new { success = false, message = _localizer["Failed_Edit_Gallery"].Value });
                                }
                            }
                            else
                            {
                                return Json(new { success = false, message = _localizer["Failed_Edit_Gallery"].Value });
                            }
                        }
                        else
                        {
                            return Json(new { success = false, message = _localizer["Gallery_Name_Already_Exists"].Value });
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"{_localizer["Failed_Edit_Gallery"].Value} - {ex?.Message}");
                    }
                }
                else
                {
                    return Json(new { success = false, message = _localizer["Name_Cannot_Be_Blank"].Value });
                }
            }

            return Json(new { success = false });
        }

        [HttpDelete]
        [RequiresRole(GalleryPermission = GalleryPermissions.Wipe)]
        public async Task<IActionResult> WipeGallery(int id)
        {
            if (User?.Identity != null && User.Identity.IsAuthenticated)
            {
                try
                {
                    var gallery = await _database.GetGallery(id);
                    if (gallery != null && User.Identity.CanEdit(GalleryPermissions.Wipe, gallery.Owner))
                    {
                        var galleryDir = Path.Combine(UploadsDirectory, gallery.Identifier);
                        if (_fileHelper.DirectoryExists(galleryDir))
                        {
                            foreach (var photo in _fileHelper.GetFiles(galleryDir, "*.*", SearchOption.AllDirectories))
                            {
                                var thumbnail = Path.Combine(ThumbnailsDirectory, gallery.Identifier, $"{Path.GetFileNameWithoutExtension(photo)}.webp");
                                _fileHelper.DeleteFileIfExists(thumbnail);
                            }

                            _fileHelper.DeleteDirectoryIfExists(galleryDir);
                            _fileHelper.CreateDirectoryIfNotExists(galleryDir);

                            if (await _settings.GetOrDefault(Notifications.Alerts.DestructiveAction, true))
                            { 
                                await _notificationHelper.Send(_localizer["Destructive_Action_Performed"].Value, $"The destructive action 'Wipe' was performed on gallery '{gallery.Name}'.", _url.GenerateBaseUrl(HttpContext?.Request, "/Account"));
                            }
                        }
                            
                        await _audit.LogAction(User?.Identity?.Name, $"{_localizer["Audit_WipedGallery"].Value} '{gallery?.Name}'");

                        return Json(new { success = await _database.WipeGallery(gallery) });
                    }
                    else
                    {
                        return Json(new { success = false, message = _localizer["Failed_Wipe_Gallery"].Value });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"{_localizer["Failed_Wipe_Gallery"].Value} - {ex?.Message}");
                }
            }

            return Json(new { success = false });
        }

        [HttpDelete]
        [RequiresRole(GalleryPermission = GalleryPermissions.Wipe)]
        public async Task<IActionResult> WipeAllGalleries()
        {
            if (User?.Identity != null && User.Identity.IsAuthenticated && (User?.Identity?.IsPrivilegedUser() ?? false))
            {
                try
                {
                    if (_fileHelper.DirectoryExists(UploadsDirectory))
                    {
                        foreach (var gallery in _fileHelper.GetDirectories(UploadsDirectory, "*", SearchOption.TopDirectoryOnly))
                        {
                            _fileHelper.DeleteDirectoryIfExists(gallery);
                        }

                        foreach (var thumbnail in _fileHelper.GetFiles(ThumbnailsDirectory, "*.*", SearchOption.AllDirectories))
                        {
                            _fileHelper.DeleteFileIfExists(thumbnail);
                        }

                        _fileHelper.CreateDirectoryIfNotExists(Path.Combine(UploadsDirectory, "default"));

                        if (await _settings.GetOrDefault(Notifications.Alerts.DestructiveAction, true))
                        {
                            await _notificationHelper.Send(_localizer["Destructive_Action_Performed"].Value, $"The destructive action 'Wipe' was performed on all galleries'.", _url.GenerateBaseUrl(HttpContext?.Request, "/Account"));
                        }
                    }
                        
                    await _audit.LogAction(User?.Identity?.Name, _localizer["Audit_WipeAllGalleries"].Value);

                    return Json(new { success = await _database.WipeAllGalleries() });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"{_localizer["Failed_Wipe_Galleries"].Value} - {ex?.Message}");
                }
            }

            return Json(new { success = false });
        }

        [HttpDelete]
        [RequiresRole(GalleryPermission = GalleryPermissions.Delete)]
        public async Task<IActionResult> DeleteGallery(int id)
        {
            if (User?.Identity != null && User.Identity.IsAuthenticated)
            {
                try
                {
                    var gallery = await _database.GetGallery(id);
                    if (gallery != null && gallery.Id > 1 && User.Identity.CanEdit(GalleryPermissions.Delete, gallery.Owner))
                    {
                        var galleryDir = Path.Combine(UploadsDirectory, gallery.Identifier);
                        _fileHelper.DeleteDirectoryIfExists(galleryDir);

                        if (await _settings.GetOrDefault(Notifications.Alerts.DestructiveAction, true))
                        {
                            await _notificationHelper.Send(_localizer["Destructive_Action_Performed"].Value, $"The destructive action 'Delete' was performed on gallery '{gallery.Name}'.", _url.GenerateBaseUrl(HttpContext?.Request, "/Account"));
                        }

                        await _audit.LogAction(User?.Identity?.Name, $"{_localizer["Audit_DeletedGallery"].Value} '{gallery?.Name} ({gallery?.OwnerName})'");

                        return Json(new { success = await _database.DeleteGallery(gallery) });
                    }
                    else
                    {
                        return Json(new { success = false, message = _localizer["Failed_Delete_Gallery"].Value });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"{_localizer["Failed_Delete_Gallery"].Value} - {ex?.Message}");
                }
            }

            return Json(new { success = false });
        }

        [HttpDelete]
        [RequiresRole(ReviewPermission = ReviewPermissions.Delete)]
        public async Task<IActionResult> DeletePhoto(int id)
        {
            if (User?.Identity != null && User.Identity.IsAuthenticated)
            {
                try
                {
                    var photo = await _database.GetGalleryItem(id);
                    if (photo != null)
                    {
                        var gallery = await _database.GetGallery(photo.GalleryId);
                        if (gallery != null && User.Identity.CanEdit(ReviewPermissions.Delete, gallery.Owner))
                        { 
                            var photoPath = Path.Combine(UploadsDirectory, gallery.Identifier, photo.Title);
                            _fileHelper.DeleteFileIfExists(photoPath);

                            await _audit.LogAction(User?.Identity?.Name, $"'{photo?.Title}' {_localizer["Audit_ItemDeletedInGallery"].Value} '{gallery?.Name}'");

                            return Json(new { success = await _database.DeleteGalleryItem(photo) });
                        }
                    }
                    else
                    {
                        return Json(new { success = false, message = _localizer["Failed_Delete_Gallery"].Value });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"{_localizer["Failed_Delete_Gallery"].Value} - {ex?.Message}");
                }
            }

            return Json(new { success = false });
        }

        [HttpPost]
        [RequiresRole(UserPermission = UserPermissions.Create)]
        public async Task<IActionResult> AddUser(UserModel model)
        {
            if (User?.Identity != null && User.Identity.IsAuthenticated && (User?.Identity?.IsPrivilegedUser() ?? false))
            {
                if (!string.IsNullOrWhiteSpace(model?.Username) && !string.IsNullOrWhiteSpace(model?.Password) && string.Equals(model.Password, model.CPassword))
                {
                    try
                    {
                        var check = await _database.GetUserByUsername(model.Username);
                        if (check == null)
                        {
                            model.Password = _encryption.Encrypt(model.Password, model.Username.ToLower());
                            model.CPassword = string.Empty;

                            await _audit.LogAction(User?.Identity?.Name, $"{_localizer["Audit_CreatedNewUser"].Value} '{model?.Username}'");

                            return Json(new { success = string.Equals(model?.Username, (await _database.AddUser(model))?.Username, StringComparison.OrdinalIgnoreCase) });
                        }
                        else
                        {
                            return Json(new { success = false, message = _localizer["User_Name_Already_Exists"].Value });
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"{_localizer["Failed_Add_User"].Value} - {ex?.Message}");
                    }
                }
                else
                {
                    return Json(new { success = false, message = _localizer["Failed_Add_User"].Value });
                }
            }

            return Json(new { success = false });
        }

        [HttpPut]
        [RequiresRole(UserPermission = UserPermissions.Update)]
        public async Task<IActionResult> EditUser(UserModel model)
        {
            if (User?.Identity != null && User.Identity.IsAuthenticated)
            {
                if (model?.Id != null)
                {
                    try
                    {
                        var user = await _database.GetUser(model.Id);
                        if (user != null && User.Identity.CanEdit(UserPermissions.Update, user.Id))
                        {
                            user.Email = model.Email;

                            if (User.Identity.IsPrivilegedUser() && User.Identity.GetUserPermissions().Users.HasFlag(UserPermissions.Change_Permissions_Level))
                            { 
                                user.Level = model.Level;
                            }
                         
                            await _audit.LogAction(User?.Identity?.Name, $"{_localizer["Audit_UpdatedUser"].Value} '{user?.Username}'");

                            return Json(new { success = string.Equals(user?.Username, (await _database.EditUser(user))?.Username, StringComparison.OrdinalIgnoreCase) });
                        }
                        else
                        {
                            return Json(new { success = false, message = _localizer["Failed_Edit_User"].Value });
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"{_localizer["Failed_Edit_User"].Value} - {ex?.Message}");
                    }
                }
                else
                {
                    return Json(new { success = false, message = _localizer["Failed_Edit_User"].Value });
                }
            }

            return Json(new { success = false });
        }

        [HttpPut]
        [RequiresRole(UserPermission = UserPermissions.Change_Password)]
        public async Task<IActionResult> ChangeUserPassword(UserModel model)
        {
            if (User?.Identity != null && User.Identity.IsAuthenticated)
            {
                if (model?.Id != null && !string.IsNullOrWhiteSpace(model?.Password) && string.Equals(model.Password, model.CPassword))
                {
                    try
                    {
                        var user = await _database.GetUser(model.Id);
                        if (user != null && User.Identity.CanEdit(UserPermissions.Change_Password, user.Id))
                        {
                            user.Password = _encryption.Encrypt(model.Password, user.Username.ToLower());

                            await _audit.LogAction(User?.Identity?.Name, $"{_localizer["Audit_UpdatedUser"].Value} '{user?.Username}'");

                            return Json(new { success = await _database.ChangePassword(user) });
                        }
                        else
                        {
                            return Json(new { success = false, message = _localizer["Failed_Edit_User"].Value });
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"{_localizer["Failed_Edit_User"].Value} - {ex?.Message}");
                    }
                }
                else
                {
                    return Json(new { success = false, message = _localizer["Failed_Edit_User"].Value });
                }
            }

            return Json(new { success = false });
        }

        [HttpPut]
        [RequiresRole(UserPermission = UserPermissions.Freeze)]
        public async Task<IActionResult> FreezeUser(int id)
        {
            if (User?.Identity != null && User.Identity.IsAuthenticated)
            {
                try
                {
                    var user = await _database.GetUser(id);
                    if (user != null && User.Identity.CanEdit(UserPermissions.Freeze, user.Id))
                    {
                        user.State = AccountState.Frozen;

                        await _audit.LogAction(User?.Identity?.Name, $"{_localizer["Audit_FrozeUser"].Value} '{user?.Username}'");

                        return Json(new { success = (await _database.EditUser(user))?.State == user.State });
                    }
                    else
                    {
                        return Json(new { success = false, message = _localizer["Failed_Edit_User"].Value });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"{_localizer["Failed_Edit_User"].Value} - {ex?.Message}");
                }
            }

            return Json(new { success = false });
        }

        [HttpPut]
        [RequiresRole(UserPermission = UserPermissions.Freeze)]
        public async Task<IActionResult> UnfreezeUser(int id)
        {
            if (User?.Identity != null && User.Identity.IsAuthenticated)
            {
                try
                {
                    var user = await _database.GetUser(id);
                    if (user != null && User.Identity.CanEdit(UserPermissions.Freeze, user.Id))
                    {
                        user.State = AccountState.Active;

                        await _audit.LogAction(User?.Identity?.Name, $"{_localizer["Audit_UnfrozeUser"].Value} '{user?.Username}'");

                        return Json(new { success = (await _database.EditUser(user))?.State == user.State });
                    }
                    else
                    {
                        return Json(new { success = false, message = _localizer["Failed_Edit_User"].Value });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"{_localizer["Failed_Edit_User"].Value} - {ex?.Message}");
                }
            }

            return Json(new { success = false });
        }

        [HttpPut]
        [RequiresRole(UserPermission = UserPermissions.Freeze)]
        public async Task<IActionResult> ActivateUser(int id)
        {
            if (User?.Identity != null && User.Identity.IsAuthenticated)
            {
                try
                {
                    var user = await _database.GetUser(id);
                    if (user != null && User.Identity.CanEdit(UserPermissions.Freeze, user.Id))
                    {
                        user.State = AccountState.Active;

                        await _database.SetUserSecret(user.Id, PasswordHelper.GenerateSecretCode());
                        await CreateDefaultUserGallery(user);

                        await _audit.LogAction(User?.Identity?.Name, $"{_localizer["Audit_ActivateUser"].Value} '{user?.Username}'");

                        return Json(new { success = (await _database.EditUser(user))?.State == user.State });
                    }
                    else
                    {
                        return Json(new { success = false, message = _localizer["Failed_Edit_User"].Value });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"{_localizer["Failed_Edit_User"].Value} - {ex?.Message}");
                }
            }

            return Json(new { success = false });
        }

        [HttpDelete]
        [RequiresRole(UserPermission = UserPermissions.Delete)]
        public async Task<IActionResult> DeleteUser(int id)
        {
            if (User?.Identity != null && User.Identity.IsAuthenticated)
            {
                try
                {
                    var user = await _database.GetUser(id);
                    if (user != null && user.Id > 1 && User.Identity.CanEdit(UserPermissions.Delete, user.Id))
                    {
                        var galleries = await _database.GetUserGalleries(user.Id);
                        foreach (var gallery in galleries)
                        {
                            await DeleteGallery(gallery.Id);
                        }

                        var customResources = await _database.GetUserCustomResources(user.Id);
                        foreach (var customResource in customResources)
                        {
                            await RemoveCustomResource(customResource.Id);
                        }

                        if (await _settings.GetOrDefault(Notifications.Alerts.DestructiveAction, true))
                        {
                            await _notificationHelper.Send(_localizer["Destructive_Action_Performed"].Value, $"The destructive action 'Delete' was performed on user '{user.Username}'.", _url.GenerateBaseUrl(HttpContext?.Request, "/Account"));
                        }

                        await _audit.LogAction(User?.Identity?.Name, $"{_localizer["Audit_DeletedUser"].Value} '{user?.Username}'");

                        return Json(new { success = await _database.DeleteUser(user) });
                    }
                    else
                    {
                        return Json(new { success = false, message = _localizer["Failed_Delete_User"].Value });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"{_localizer["Failed_Delete_User"].Value} - {ex?.Message}");
                }
            }

            return Json(new { success = false });
        }

        [HttpPut]
        [RequiresRole(SettingsPermission = SettingsPermissions.Update)]
        public async Task<IActionResult> UpdateSettings(List<UpdateSettingsModel> model)
        {
            return await UpdateSettings(model, null, SettingsPermissions.Update);
        }

        [HttpPut]
        [RequiresRole(SettingsPermission = SettingsPermissions.Gallery_Update)]
        public async Task<IActionResult> UpdateGallerySettings(List<UpdateSettingsModel> model, int galleryId)
        {
            return await UpdateSettings(model, galleryId, SettingsPermissions.Gallery_Update);
        }

        [HttpPost]
        [RequiresRole(DataPermission = DataPermissions.Export)]
        public async Task<IActionResult> ExportBackup(ExportOptions options)
        {
            if (User?.Identity != null && User.Identity.IsAuthenticated && (User?.Identity?.IsPrivilegedUser() ?? false))
            {
                var exportDir = Path.Combine(TempDirectory, "Export");

                try
                {
                    if (_fileHelper.DirectoryExists(UploadsDirectory))
                    {
                        var databaseImportExport = string.Equals("sqlite", await _settings.GetOrDefault(Settings.Database.Type, "sqlite"), StringComparison.OrdinalIgnoreCase);

                        _fileHelper.CreateDirectoryIfNotExists(TempDirectory);
                        _fileHelper.DeleteDirectoryIfExists(exportDir);
                        _fileHelper.CreateDirectoryIfNotExists(exportDir);

                        var dbExport = Path.Combine(exportDir, $"WeddingShare.bak");

                        var exported = true;
                        if (databaseImportExport && options.Database)
                        { 
                            exported = await _database.Export($"Data Source={dbExport}");
                        }

                        if (exported)
                        {
                            var listing = new List<ZipListing>();

                            if (databaseImportExport && options.Database)
                            {
                                listing.Add(new ZipListing(exportDir, new string[] { dbExport }));
                            }

                            if (options.Uploads)
                            {
                                listing.Add(new ZipListing(UploadsDirectory, Directory.GetFiles(UploadsDirectory, "*", SearchOption.AllDirectories), null, "Uploads.bak"));
                            }

                            if (options.Thumbnails)
                            {
                                listing.Add(new ZipListing(ThumbnailsDirectory, Directory.GetFiles(ThumbnailsDirectory, "*", SearchOption.AllDirectories), null, "Thumbnails.bak"));
                            }

                            if (options.CustomResources && _fileHelper.DirectoryExists(CustomResourcesDirectory))
                            {
                                listing.Add(new ZipListing(CustomResourcesDirectory, Directory.GetFiles(CustomResourcesDirectory, "*", SearchOption.AllDirectories), null, "CustomResources.bak"));
                            }

                            var response = await ZipFileResponse($"WeddingShare-{DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss")}.zip", listing);

                            _fileHelper.DeleteFileIfExists(dbExport);

                            await _audit.LogAction(User?.Identity?.Name, _localizer["Audit_ExportedBackup"].Value);

                            return response;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"{_localizer["Failed_Export"].Value} - {ex?.Message}");
                }
                finally
                {
                    _fileHelper.DeleteDirectoryIfExists(exportDir);
                }
            }

            return Json(new { success = false });
        }

        [HttpPost]
        [RequiresRole(DataPermission = DataPermissions.Import)]
        public async Task<IActionResult> ImportBackup()
        {
            if (User?.Identity != null && User.Identity.IsAuthenticated && (User?.Identity?.IsPrivilegedUser() ?? false))
            {
                var importDir = Path.Combine(TempDirectory, "Import");

                try
                {
                    var files = Request?.Form?.Files;
                    if (files != null && files.Count > 0)
                    {
                        foreach (IFormFile file in files)
                        {
                            var extension = Path.GetExtension(file.FileName)?.Trim('.');
                            if (string.Equals("zip", extension, StringComparison.OrdinalIgnoreCase))
                            {
                                _fileHelper.CreateDirectoryIfNotExists(TempDirectory);

                                var filePath = Path.Combine(TempDirectory, "Import.zip");
                                if (!string.IsNullOrWhiteSpace(filePath))
                                {
									await _fileHelper.SaveFile(file, filePath, FileMode.Create);

									_fileHelper.DeleteDirectoryIfExists(importDir);
                                    _fileHelper.CreateDirectoryIfNotExists(importDir);

                                    ZipFile.ExtractToDirectory(filePath, importDir, true);
                                    _fileHelper.DeleteFileIfExists(filePath);

                                    var uploadsZip = Path.Combine(importDir, "Uploads.bak");
                                    ZipFile.ExtractToDirectory(uploadsZip, UploadsDirectory, true);

                                    var thumbnailsZip = Path.Combine(importDir, "Thumbnails.bak");
                                    ZipFile.ExtractToDirectory(thumbnailsZip, ThumbnailsDirectory, true);

                                    var customResourcesZip = Path.Combine(importDir, "CustomResources.bak");
                                    if (_fileHelper.FileExists(customResourcesZip))
                                    {
                                        ZipFile.ExtractToDirectory(customResourcesZip, CustomResourcesDirectory, true);
                                    }

                                    var dbImport = Path.Combine(importDir, "WeddingShare.bak");
                                    var imported = await _database.Import($"Data Source={dbImport}");

                                    await _audit.LogAction(User?.Identity?.Name, _localizer["Audit_ImportedBackup"].Value);

                                    return Json(new { success = imported });
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"{_localizer["Import_Failed"].Value} - {ex?.Message}");
                }
                finally
                {
                    _fileHelper.DeleteDirectoryIfExists(importDir);
                }
            }

            return Json(new { success = false });
        }

        [HttpPost]
        [RequiresRole(UserPermission = UserPermissions.Login)]
        public async Task<IActionResult> RegisterMultifactorAuth(string secret, string code)
        {
            if (!string.IsNullOrWhiteSpace(secret) && !string.IsNullOrWhiteSpace(code))
            {
                if (User?.Identity != null && User.Identity.IsAuthenticated && await _settings.GetOrDefault(Settings.IsDemoMode, false) == false)
                {
                    try
                    {
                        var tfa = new TwoFactorAuth(await _settings.GetOrDefault(Settings.Basic.Title, "WeddingShare"));
                        if (tfa.VerifyCode(secret, code))
                        {
                            var userId = User.Identity.GetUserId();
                            if (userId > 0)
                            {
                                var set = await _database.SetMultiFactorToken(userId, secret);
                                if (set)
                                {
                                    await _audit.LogAction(User?.Identity?.Name, _localizer["Audit_MultiFactorAdded"].Value);

                                    HttpContext.Session.SetString(SessionKey.MultiFactorTokenSet, "true");
                                    return Json(new { success = true });
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"{_localizer["MultiFactor_Token_Set_Failed"].Value} - {ex?.Message}");
                    }
                }
            }

            return Json(new { success = false });
        }

        [HttpDelete]
        [RequiresRole(UserPermission = UserPermissions.Reset_MFA)]
        public async Task<IActionResult> ResetMultifactorAuth()
        {
            if (User?.Identity != null && User.Identity.IsAuthenticated)
            {
                try
                {
                    await _audit.LogAction(User?.Identity?.Name, _localizer["Audit_MultiFactorReset"].Value);

                    return await ResetMultifactorAuthForUser(User.Identity.GetUserId());
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"{_localizer["MultiFactor_Token_Set_Failed"].Value} - {ex?.Message}");
                }
            }

            return Json(new { success = false });
        }

        [HttpDelete]
        [RequiresRole(UserPermission = UserPermissions.Reset_MFA)]
        public async Task<IActionResult> ResetMultifactorAuthForUser(int userId)
        {
            if (User?.Identity != null && User.Identity.IsAuthenticated)
            {
                try
                {
                    if (userId > 0)
                    {
                        var user = await _database.GetUser(userId);
                        if (user != null && User.Identity.CanEdit(UserPermissions.Reset_MFA, user.Id))
                        { 
                            var cleared = await _database.SetMultiFactorToken(userId, string.Empty);
                            if (cleared)
                            {
                                var currentUserId = User.Identity.GetUserId();
                                if (userId == currentUserId)
                                { 
                                    HttpContext.Session.SetString(SessionKey.MultiFactorTokenSet, "false");
                                }

                                await _audit.LogAction(User?.Identity?.Name, $"{_localizer["Audit_MultiFactorResetUser"].Value} '{user?.Username}'");

                                return Json(new { success = true });
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"{_localizer["MultiFactor_Token_Set_Failed"].Value} - {ex?.Message}");
                }
            }

            return Json(new { success = false });
        }

        [HttpPost]
        [RequiresRole(CustomResourcePermission = CustomResourcePermissions.Create)]
        public async Task<IActionResult> UploadCustomResource()
        {
            Response.StatusCode = (int)HttpStatusCode.BadRequest;

            if (User?.Identity != null && User.Identity.IsAuthenticated)
            {
                try
                {
                    var files = Request?.Form?.Files;
                    if (files != null && files.Count > 0)
                    {
                        var userId = User.Identity.GetUserId();

                        var uploaded = 0;
                        var errors = new List<string>();
                        foreach (IFormFile file in files)
                        {
                            try
                            {
                                var fileName = User.Identity.IsBasicUser() ? $"{userId}_{file.FileName}" : file.FileName;
                                var filePath = Path.Combine(CustomResourcesDirectory, fileName);
                                if (string.IsNullOrWhiteSpace(filePath))
                                {
                                    continue;
                                }
                                else if (_fileHelper.FileExists(filePath))
                                {
                                    errors.Add($"{_localizer["File_Upload_Failed"].Value}. {_localizer["Filename_Already_Exists"].Value}");
                                }
                                else
                                {
                                    _fileHelper.CreateDirectoryIfNotExists(CustomResourcesDirectory);
                                    await _fileHelper.SaveFile(file, filePath, FileMode.Create);

                                    var item = await _database.AddCustomResource(new CustomResourceModel()
                                    {
                                        FileName = fileName,
                                        UploadedBy = User?.Identity.Name,
                                        Owner = userId
                                    });

                                    if (item?.Id > 0)
                                    {
                                        uploaded++;
                                        await _audit.LogAction(User?.Identity?.Name, $"{_localizer["Audit_CustomResourceUploaded"].Value} '{item?.FileName}'");
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, $"{_localizer["Save_To_Custom_Resources_Failed"].Value} - {ex?.Message}");
                            }
                        }

                        Response.StatusCode = (int)HttpStatusCode.OK;

                        return Json(new { success = uploaded > 0, errors });
                    }
                    else
                    {
                        return Json(new { success = false, errors = new List<string>() { _localizer["No_Files_For_Upload"].Value } });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"{_localizer["CustomResource_Upload_Failed"].Value} - {ex?.Message}");
                }
            }

            return Json(new { success = false });
        }

        [HttpDelete]
        [RequiresRole(CustomResourcePermission = CustomResourcePermissions.Delete)]
        public async Task<IActionResult> RemoveCustomResource(int id)
        {
            Response.StatusCode = (int)HttpStatusCode.BadRequest;

            if (User?.Identity != null && User.Identity.IsAuthenticated)
            {
                try
                {
                    var resource = await _database.GetCustomResource(id);
                    if (resource != null && User.Identity.CanEdit(CustomResourcePermissions.Delete, resource.Owner))
                    {
                        if (await _database.DeleteCustomResource(resource))
                        {
                            if (!string.IsNullOrWhiteSpace(resource.FileName))
                            { 
                                _fileHelper.DeleteFileIfExists(Path.Combine(CustomResourcesDirectory, resource.FileName));
                            }

                            await _audit.LogAction(User?.Identity?.Name, $"{_localizer["Audit_CustomResourceDeleted"].Value} '{resource?.FileName}'");

                            Response.StatusCode = (int)HttpStatusCode.OK;

                            return Json(new { success = true });
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"{_localizer["CustomResource_Delete_Failed"].Value} - {ex?.Message}");
                }
            }

            return Json(new { success = false });
        }

        [HttpGet]
        [RequiresRole(UserPermission = UserPermissions.Login)]
        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public async Task<IActionResult> CheckAccountState()
        {
            Response.StatusCode = (int)HttpStatusCode.BadRequest;

            if (User?.Identity != null && User.Identity.IsAuthenticated)
            {
                try
                {
                    var user = await _database.GetUser(User.Identity.GetUserId());
                    if (user != null && User.Identity.CanEdit(UserPermissions.Login, user.Id))
                    {
                        Response.StatusCode = (int)HttpStatusCode.OK;

                        return Json(new { active = user.State == AccountState.Active });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"{_localizer["Check_Account_State_Failed"].Value} - {ex?.Message}");
                }
            }

            return Json(new { active = false });
        }

        private async Task<IActionResult> UpdateSettings(List<UpdateSettingsModel> model, int? galleryId, SettingsPermissions accessPermissions)
        {
            if (User?.Identity != null && User.Identity.IsAuthenticated)
            {
                if (model != null && model.Count() > 0)
                {
                    try
                    {
                        var success = true;

                        GalleryModel? gallery = null;
                        if (galleryId != null)
                        {
                            gallery = await _database.GetGallery((int)galleryId);
                        }

                        if (User.Identity.CanEdit(accessPermissions, gallery?.Owner))
                        {
                            foreach (var m in model)
                            {
                                try
                                {
                                    var setting = await _database.SetSetting(new SettingModel()
                                    {
                                        Id = m.Key,
                                        Value = m.Value
                                    }, gallery?.Id);

                                    if (setting == null || (setting.Value ?? string.Empty) != (m.Value ?? string.Empty))
                                    {
                                        success = false;
                                    }
                                    else
                                    {
                                        await _audit.LogAction(User?.Identity?.Name, $"{_localizer["Audit_SettingsUpdated"].Value} '{(!string.IsNullOrWhiteSpace(gallery?.Name) ? gallery.Name : "Gallery Defaults")}' - '{setting?.Id}'='{setting?.Value}'");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(ex, $"{_localizer["Failed_Update_Setting"].Value} - {ex?.Message}");
                                }
                            }
                        }

                        return Json(new { success = success });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"{_localizer["Failed_Update_Setting"].Value} - {ex?.Message}");
                    }
                }
                else
                {
                    return Json(new { success = false, message = _localizer["Failed_Update_Setting"].Value });
                }
            }

            return Json(new { success = false });
        }

        private async Task<bool> SetUserClaims(HttpContext ctx, UserModel user)
        {
            try
            {
                var claims = new List<Claim>
                {
                    new Claim(ClaimTypes.Sid, user.Id.ToString()),
                    new Claim(ClaimTypes.Name, user.Username.ToLower()),
                    new Claim(ClaimTypes.Role, user.Level.ToString()),
                };

                var claimsIdentity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);

                await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(claimsIdentity));

                return true;
            }
            catch
            {
                return false;
            }
        }

        private async Task<bool> FailedLoginDetected(LoginModel model, UserModel user)
        {
            try
            {
                if (await _settings.GetOrDefault(Notifications.Alerts.FailedLogin, true))
                {
                    var ipAddress = Request.HttpContext.TryGetIpAddress();
                    var country = Request.HttpContext.TryGetCountry();

                    await _notificationHelper.Send("Invalid Login Detected", $"An invalid login attempt was made for account '{model?.Username}' from ip address '{ipAddress}' based in country '{country}'.", _url.GenerateBaseUrl(HttpContext?.Request, "/Account"));
                }

                var failedAttempts = await _database.IncrementLockoutCount(user.Id);
                if (failedAttempts >= await _settings.GetOrDefault(Settings.Account.LockoutAttempts, 5))
                {
                    var timeout = await _settings.GetOrDefault(Settings.Account.LockoutMins, 60);
                    await _database.SetLockout(user.Id, DateTime.UtcNow.AddMinutes(timeout));

                    if (await _settings.GetOrDefault(Notifications.Alerts.AccountLockout, true))
                    {
                        await _notificationHelper.Send("Account Lockout", $"Account '{model?.Username}' has been locked out for {timeout} minutes due to too many failed login attempts.", _url.GenerateBaseUrl(HttpContext?.Request, "/Account"));
                    }
                }

                await _audit.LogAction(user?.Username, _localizer["Audit_FailedLoginAttemptDetected"].Value);

                return true;
            }
            catch 
            {
                return false;
            }
        }

        private async Task<List<PhotoGallery>> GetPendingReviews(int? userId = null)
        {
            var galleries = new List<PhotoGallery>();

            var items = userId != null ? await _database.GetUserPendingGalleryItems((int)userId) : await _database.GetPendingGalleryItems();
            if (items != null)
            {
                foreach (var galleryGroup in items.GroupBy(x => x.GalleryId))
                {
                    var gallery = await _database.GetGallery(galleryGroup.Key);
                    if (gallery != null)
                    {
                        galleries.Add(new PhotoGallery()
                        {
                            Gallery = gallery,
                            Images = items?.Select(x => new PhotoGalleryImage()
                            {
                                Id = x.Id,
                                GalleryId = x.GalleryId,
                                Name = Path.GetFileName(x.Title),
                                UploadedBy = x.UploadedBy,
                                UploaderEmailAddress = x.UploaderEmailAddress,
                                UploadDate = x.UploadedDate,
                                ImagePath = $"/{Path.Combine(UploadsDirectory, gallery.Identifier).Remove(_hostingEnvironment.WebRootPath).Replace('\\', '/').TrimStart('/')}/Pending/{x.Title}",
                                ThumbnailPath = $"/{Path.Combine(ThumbnailsDirectory, gallery.Identifier).Remove(_hostingEnvironment.WebRootPath).Replace('\\', '/').TrimStart('/')}/{Path.GetFileNameWithoutExtension(x.Title)}.webp",
                                MediaType = x.MediaType
                            })?.ToList(),
                            ItemsPerPage = int.MaxValue,
                        });
                    }
                }
            }

            return galleries;
        }

        private async Task<GalleryModel?> CreateDefaultUserGallery(UserModel user)
        {
            GalleryModel? gallery = null;

            if (user != null)
            { 
                try
                {
                    gallery = await _database.AddGallery(new GalleryModel()
                    {
                        Name = DefaultUserGalleryName,
                        SecretKey = PasswordHelper.GenerateGallerySecretKey(),
                        Owner = user.Id
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to create '{DefaultUserGalleryName}' gallery for user '{user.Username}'");
                }
            }

            return gallery;
        }
    }
}
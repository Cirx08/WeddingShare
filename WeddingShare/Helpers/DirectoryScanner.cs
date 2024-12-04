﻿using NCrontab;
using WeddingShare.Enums;
using WeddingShare.Helpers.Database;
using WeddingShare.Models.Database;

namespace WeddingShare.Helpers
{
    public sealed class DirectoryScanner(IWebHostEnvironment hostingEnvironment, IConfigHelper configHelper, IDatabaseHelper databaseHelper, IImageHelper imageHelper, ILogger<DirectoryScanner> logger) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var cron = configHelper.GetOrDefault("BackgroundServices", "Directory_Scanner_Interval", "*/30 * * * *");
            var schedule = CrontabSchedule.Parse(cron);

            await Task.Delay((int)TimeSpan.FromSeconds(10).TotalMilliseconds, stoppingToken);
            await ScanForFiles();

            while (!stoppingToken.IsCancellationRequested)
            {
                var now = DateTime.Now;
                var nextExecutionTime = schedule.GetNextOccurrence(now);
                var waitTime = nextExecutionTime - now;
                await Task.Delay(waitTime, stoppingToken);

                await ScanForFiles();
            }
        }
                
        private async Task ScanForFiles()
        {
            await Task.Run(async () =>
            {
                var allowedFileTypes = configHelper.GetOrDefault("Settings", "Allowed_File_Types", ".jpg,.jpeg,.png").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

                var thumbnailsDirectory = Path.Combine(hostingEnvironment.WebRootPath, "thumbnails");
                if (!Directory.Exists(thumbnailsDirectory))
                {
                    Directory.CreateDirectory(thumbnailsDirectory);
                }

                var uploadsDirectory = Path.Combine(hostingEnvironment.WebRootPath, "uploads");
                if (Directory.Exists(uploadsDirectory))
                {
                    var searchPattern = !configHelper.GetOrDefault("Settings", "Single_Gallery_Mode", false) ? "*" : "default";
                    var galleries = Directory.GetDirectories(uploadsDirectory, searchPattern, SearchOption.TopDirectoryOnly)?.Where(x => !Path.GetFileName(x).StartsWith("."));
                    if (galleries != null)
                    {
                        foreach (var gallery in galleries)
                        {
                            try
                            {
                                var id = Path.GetFileName(gallery).ToLower();
                                var galleryItem = await databaseHelper.GetGallery(id);
                                if (galleryItem == null)
                                {
                                    galleryItem = await databaseHelper.AddGallery(new GalleryModel()
                                    {
                                        Name = id
                                    });
                                }

                                if (galleryItem != null)
                                {
                                    var galleryItems = await databaseHelper.GetAllGalleryItems(galleryItem.Id);

                                    if (Path.Exists(gallery))
                                    { 
                                        var approvedFiles = Directory.GetFiles(gallery, "*.*", SearchOption.TopDirectoryOnly).Where(x => allowedFileTypes.Any(y => string.Equals(Path.GetExtension(x).Trim('.'), y.Trim('.'), StringComparison.OrdinalIgnoreCase)));
                                        if (approvedFiles != null)
                                        {
                                            foreach (var file in approvedFiles)
                                            {
                                                try
                                                {
                                                    var filename = Path.GetFileName(file);
                                                    if (!galleryItems.Exists(x => string.Equals(x.Title, filename, StringComparison.OrdinalIgnoreCase)))
                                                    {
                                                        await databaseHelper.AddGalleryItem(new GalleryItemModel()
                                                        {
                                                            GalleryId = galleryItem.Id,
                                                            Title = filename,
                                                            State = GalleryItemState.Approved
                                                        });
                                                    }

                                                    var thumbnailPath = Path.Combine(thumbnailsDirectory, $"{Path.GetFileNameWithoutExtension(file)}.webp");
                                                    if (!File.Exists(thumbnailPath))
                                                    {
                                                        await imageHelper.GenerateThumbnail(file, thumbnailPath, configHelper.GetOrDefault("Settings", "Thumbnail_Size", 720));
                                                    }
                                                }
                                                catch (Exception ex)
                                                {
                                                    logger.LogError(ex, $"An error occurred while scanning file '{file}'");
                                                }
                                            }
                                        }

                                        if (Path.Exists(Path.Combine(gallery, "Pending")))
                                        { 
                                            var pendingFiles = Directory.GetFiles(Path.Combine(gallery, "Pending"), "*.*", SearchOption.TopDirectoryOnly).Where(x => allowedFileTypes.Any(y => string.Equals(Path.GetExtension(x).Trim('.'), y.Trim('.'), StringComparison.OrdinalIgnoreCase)));
                                            if (pendingFiles != null)
                                            {
                                                foreach (var file in pendingFiles)
                                                {
                                                    try
                                                    {
                                                        var filename = Path.GetFileName(file);
                                                        if (!galleryItems.Exists(x => string.Equals(x.Title, filename, StringComparison.OrdinalIgnoreCase)))
                                                        {
                                                            await databaseHelper.AddGalleryItem(new GalleryItemModel()
                                                            {
                                                                GalleryId = galleryItem.Id,
                                                                Title = filename,
                                                                State = GalleryItemState.Pending
                                                            });
                                                        }
                                                    }
                                                    catch (Exception ex)
                                                    {
                                                        logger.LogError(ex, $"An error occurred while scanning file '{file}'");
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                logger.LogError(ex, $"An error occurred while scanning directory '{gallery}'");
                            }
                        }
                    }
                }
            });
        }
    }
}
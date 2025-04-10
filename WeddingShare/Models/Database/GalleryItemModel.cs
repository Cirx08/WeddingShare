﻿using WeddingShare.Enums;

namespace WeddingShare.Models.Database
{
    public class GalleryItemModel
    {
        public GalleryItemModel()
            : this(0, 0, string.Empty, string.Empty, null, null, null, MediaType.Unknown, ImageOrientation.None, GalleryItemState.Pending)
        {
        }

        public GalleryItemModel(int id, int galleryId, string galleryName, string title, string? uploadedBy, DateTime? uploadedDate, string? checksum, MediaType mediaType, ImageOrientation orientation, GalleryItemState state)
        {
            Id = id;
            GalleryId = galleryId;
            GalleryName = galleryName;
            Title = title;
            UploadedBy = uploadedBy;
            UploadedDate = uploadedDate;
            Checksum = checksum;
            MediaType = mediaType;
            Orientation = orientation;
            State = state;
        }

        public int Id { get; set; }
        public int GalleryId { get; set; }
        public string GalleryName { get; set; }
        public string Title { get; set; }
        public string? UploadedBy { get; set; }
        public DateTime? UploadedDate { get; set; }
        public string? Checksum { get; set; }
        public MediaType MediaType { get; set; }
        public ImageOrientation Orientation { get; set; }
        public GalleryItemState State { get; set; }
    }
}
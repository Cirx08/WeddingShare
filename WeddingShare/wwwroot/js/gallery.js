﻿(function () {
    document.addEventListener('DOMContentLoaded', function () {

        const triggerSelector = event => {
            const zone = event.target.closest('.upload_drop') || false;
            const input = zone.querySelector('input[type="file"]') || false;
            input.click();
        }

        const highlight = event =>
            event.target.classList.add('highlight');

        const unhighlight = event =>
            event.target.classList.remove('highlight');

        const getInputAndGalleryRefs = element => {
            const zone = element.closest('.upload_drop') || false;
            const gallery = zone.querySelector('.upload_gallery') || false;
            const input = zone.querySelector('input[type="file"]') || false;
            return { input: input, gallery: gallery };
        }

        const handleDrop = event => {
            const dataRefs = getInputAndGalleryRefs(event.target);
            dataRefs.files = event.dataTransfer.files;
            handleFiles(dataRefs);
        }

        const eventHandlers = zone => {

            const dataRefs = getInputAndGalleryRefs(zone);

            if (!dataRefs.input) return;

            // Prevent default drag behaviors
            ;['dragenter', 'dragover', 'dragleave', 'drop'].forEach(event => {
                zone.addEventListener(event, preventDefaults, false);
                document.body.addEventListener(event, preventDefaults, false);
            });

            // Open file browser on drop area click
            ;['click', 'touch'].forEach(event => {
                zone.addEventListener(event, triggerSelector, false);
            });
            // Highlighting drop area when item is dragged over it
            ;['dragenter', 'dragover'].forEach(event => {
                zone.addEventListener(event, highlight, false);
            });
            ;['dragleave', 'drop'].forEach(event => {
                zone.addEventListener(event, unhighlight, false);
            });

            // Handle dropped files
            zone.addEventListener('drop', handleDrop, false);

            // Handle browse selected files
            dataRefs.input.addEventListener('change', event => {
                dataRefs.files = event.target.files;
                handleFiles(dataRefs);
            }, false);

        }

        // Initialise ALL dropzones
        const dropZones = document.querySelectorAll('.upload_drop');
        for (const zone of dropZones) {
            eventHandlers(zone);
        }

        // No 'image/gif' or PDF or webp allowed here, but it's up to your use case.
        // Double checks the input "accept" attribute
        const isImageFile = file => file.type.toLowerCase().startsWith('image/');

        // Based on: https://flaviocopes.com/how-to-upload-files-fetch/
        const imageUpload = async dataRefs => {

            // Multiple source routes, so double check validity
            if (!dataRefs.files || !dataRefs.input) {
                displayMessage(`Upload`, `No files were detected to upload`);
                return;
            }

            const galleryId = dataRefs.input.getAttribute('data-post-gallery-id');
            if (!galleryId) {
                displayMessage(`Upload`, `Invalid gallery Id detected`);
                return;
            }

            const url = dataRefs.input.getAttribute('data-post-url');
            if (!url) {
                displayMessage(`Upload`, `Could not find upload Url`);
                return;
            }

            const secretKey = dataRefs.input.getAttribute('data-post-key');
            const uploadedBy = getCookie('ViewerIdentity');

            let uploadedCount = 0;
            let requiresReview = true;

            for (var i = 0; i < dataRefs.files.length; i++) {
                const formData = new FormData();
                formData.append('Id', galleryId);
                formData.append('SecretKey', secretKey);
                formData.append('UploadedBy', uploadedBy);
                formData.append(dataRefs.files[i].name, dataRefs.files[i]);

                displayLoader(`Uploading photo ${i + 1} of ${dataRefs.files.length}...`);

                let response = await postData({ url, formData });
                if (response !== undefined && response.success === true) {
                    uploadedCount++;
                    requiresReview = response.requiresReview;
                }
            }

            hideLoader();

            if (requiresReview) {
                displayMessage(`Upload`, `Successfully uploaded ${uploadedCount} photo(s) pending review`);

                const formData = new FormData();
                formData.append('Id', galleryId);
                formData.append('SecretKey', secretKey);
                formData.append('UploadedBy', uploadedBy);
                formData.append('Count', uploadedCount);

                postData({ url: '/Gallery/UploadCompleted', formData });
            } else {
                displayMessage(`Upload`, `Successfully uploaded ${uploadedCount} photo(s)`);
            }
        }

        const postData = async request => {
            return fetch(request.url, {
                method: 'POST',
                body: request.formData
            })
                .then(response => response.json())
                .then(data => {
                    return data;
                });
        }

        // Handle both selected and dropped files
        const handleFiles = async dataRefs => {

            let files = [...dataRefs.files];

            // Remove unaccepted file types
            files = files.filter(item => {
                var isImage = isImageFile(item);
                if (!isImage) {
                    console.log('Not an image, ', item.type);
                }

                return isImage ? item : null;
            });

            if (!files.length) return;
            dataRefs.files = files;

            await imageUpload(dataRefs);
        }

        $(document).off('click', 'button.btnDeletePhoto').on('click', 'button.btnDeletePhoto', function (e) {
            preventDefaults(e);

            if ($(this).attr('disabled') == 'disabled') {
                return;
            }

            var id = $(this).data('photo-id');
            var name = $(this).data('photo-name');

            displayPopup({
                Title: 'Delete Photo',
                Message: `Are you sure you want to delete photo '${name}'?`,
                Fields: [{
                    Id: 'photo-id',
                    Value: id,
                    Type: 'hidden'
                }],
                Buttons: [{
                    Text: 'Delete',
                    Class: 'btn-danger',
                    Callback: function () {
                        displayLoader('Loading...');

                        let id = $('#popup-modal-field-photo-id').val();
                        if (id == undefined || id.length == 0) {
                            displayMessage(`Delete Photo`, `Photo id cannot be empty`);
                            return;
                        }

                        $.ajax({
                            url: '/Admin/DeletePhoto',
                            method: 'DELETE',
                            data: { id }
                        })
                            .done(data => {
                                if (data.success === true) {
                                    $(`tr[data-gallery-id=${id}]`).remove();
                                    displayMessage(`Delete Photo`, `Successfully deleted photo`);
                                } else if (data.message) {
                                    displayMessage(`Delete Photo`, `Delete failed`, [data.message]);
                                } else {
                                    displayMessage(`Delete Photo`, `Failed to delete photo`);
                                }
                            })
                            .fail((xhr, error) => {
                                displayMessage(`Delete Photo`, `Delete failed`, [error]);
                            });
                    }
                }, {
                    Text: 'Close'
                }]
            });
        });

    });
})();
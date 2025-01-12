let mediaViewerTimeout = null;
let playButtonTimeout = null;

function openMediaViewer(e) {
    let thumbnail = $($(e).find('img')[0]).attr('src');
    let source = $(e).attr('href');
    let index = $(e).data('media-viewer-index');
    let type = $(e).data('media-viewer-type');
    let title = $(e).data('media-viewer-title');
    let collection = $(e).data('media-viewer-collection');
    let author = $(e).data('media-viewer-author');
    let description = $(e).data('media-viewer-description');
    let download = $(e).data('media-viewer-download');

    displayMediaViewer(index, thumbnail, source, type, title, collection, author, description, download);
}

function displayMediaViewer(index, thumbnail, source, type, title, collection, author, description, download) {
    hideMediaViewer();

    $('body').append(`
        <div id="media-viewer-popup" style="opacity: 0;">
            <div class="media-viewer" data-media-viewer-index="${index}" data-media-viewer-collection="${collection}" data-media-viewer-source="${source}">
                <div class="media-viewer-title ${title === undefined || title.length === 0 ? 'd-none' : ''}"></div>
                <div class="media-viewer-content"><img class="media-viewer-image" src="${type.toLowerCase() === 'image' ? source : thumbnail}" /></div>
                <div class="media-viewer-author ${author === undefined || author.length === 0 ? 'd-none' : ''}"></div>
                <div class="media-viewer-description ${description === undefined || description.length === 0 ? 'd-none' : ''}"></div>
                <div class="media-viewer-options">
                    <i class="fa fa-download ${download !== undefined && download === false ? 'd-none' : ''}" onclick="download();"></i>
                    <i class="fa fa-close" onclick="hideMediaViewer();"></i>
                </div>
            </div>
        </div>
    `);

    $('#media-viewer-popup .media-viewer-title').text(title);
    $('#media-viewer-popup .media-viewer-author').text(author);
    $('#media-viewer-popup .media-viewer-description').text(description);

    clearTimeout(mediaViewerTimeout);
    mediaViewerTimeout = setTimeout(function () {
        let margin = window.innerWidth > 900 ? 50 : 20;

        let popup = $('#media-viewer-popup');
        let container = popup.find('.media-viewer');
        let mediaContainer = container.find('.media-viewer-content');
        let media = mediaContainer.find('img');

        let targetWidth = popup.innerWidth() - (margin * 2);
        let targetHeight = popup.innerHeight() - (margin * 2);

        media.width(container.innerWidth());

        let step = 1000;
        for (let i = 0; i < 5; i++) {
            let breaker = 0;
            while (breaker < 100 && container.outerWidth() < targetWidth && container.outerHeight() < targetHeight) {
                media.width(media.width() + 10);
                breaker++;
            }

            step /= 10;

            if (step < 1) {
                break;
            }
        }

        container.css({
            'top': `${(popup.innerHeight() - container.outerHeight()) / 2}px`,
            'left': `${(popup.innerWidth() - container.outerWidth()) / 2}px`
        });

        if (type === 'video') {
            let width = $('.media-viewer-content img').innerWidth();
            let height = $('.media-viewer-content img').innerHeight();
            $('.media-viewer-content').html(`
                <video width="${width}" height="${height}" controls autoplay>
                    <source src="${source}" type="video/mp4">
                    ${localization.translate('Browser_Does_Not_Support')}
                </video>
            `);
        }

        popup.fadeTo(500, 1.0);
    }, 100);
}

function hideMediaViewer() {
    $('div#media-viewer-popup').hide();
    $('div#media-viewer-popup').remove();
}

function download() {
    let source = $('#media-viewer-popup .media-viewer').data('media-viewer-source');
    let parts = source.split('/');

    let a = document.createElement('a');
    a.href = source;
    a.download = parts[parts.length - 1];
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
}

function getOrientation(item) {
    let width = item.width();
    let height = item.height();

    let orientation = 'unkown';
    if (width > height) {
        orientation = 'horizontal';
    } else if (width < height) {
        orientation = 'vertical';
    } else {
        orientation = 'square';
    }

    return orientation;
}

(function () {
    document.addEventListener('DOMContentLoaded', function () {

        clearTimeout(playButtonTimeout);
        playButtonTimeout = setTimeout(function () {
            let collections = [];
            $('.media-viewer-item').each(function () {
                let name = $(this).data('media-viewer-collection');
                if (!collections.includes(name)) {
                    collections.push(name);
                    $(`*[data-media-viewer-collection='${name}']`).each(function (i) {
                        $(this).attr('data-media-viewer-index', i);
                    });
                }
            });

            $('.media-viewer-item .media-viewer-play').each(function () {
                let element = $(this);
                let preview = element.parent();
                let thumbnail = $(preview.find('img')[0]);

                let adjustSizeFn = function () {
                    let size = element.height();
                    preview.css('height', `${thumbnail.outerHeight()}px`);

                    element.css({
                        'top': `-${(thumbnail.outerHeight() / 2)}px`,
                        'left': `${(thumbnail.outerWidth() / 2)}px`,
                        'margin-top': `-${size / 2}px`,
                        'margin-left': `-${size / 2}px`
                    });
                    element.fadeTo(200, 1.0);
                }

                thumbnail.on('load', adjustSizeFn);
                element.on('load', adjustSizeFn);
                adjustSizeFn();
            });
        }, 200);

        $(document).off('click', '.media-viewer-item').on('click', '.media-viewer-item', function (e) {
            e.preventDefault();
            e.stopPropagation();

            openMediaViewer(this);
        });

        $(document).off('click', '.media-viewer .media-viewer-content').on('click', '.media-viewer .media-viewer-content', function (e) {
            e.preventDefault();
            e.stopPropagation();

            let viewer = $(this).closest('.media-viewer');
            let index = viewer.data('media-viewer-index');
            let collection = viewer.data('media-viewer-collection');
            let items = $(`a[data-media-viewer-collection='${collection}']`);

            let position = e.pageX - $(this).offset().left;
            if (position <= ($(this).width() / 2)) {
                index--;
                if (index < 0) {
                    index = items.length - 1;
                }
            } else {
                index++;
                if (index >= items.length) {
                    index = 0;
                }
            }

            openMediaViewer(items[index]);
        });

        $(document).off('click', 'div#media-viewer-popup').on('click', 'div#media-viewer-popup', function (e) {
            e.preventDefault();
            e.stopPropagation();
            hideMediaViewer();
        });

        $(document).off('click', 'div.media-viewer').on('click', 'div.media-viewer', function (e) {
            e.preventDefault();
            e.stopPropagation();
        });

    });
})();
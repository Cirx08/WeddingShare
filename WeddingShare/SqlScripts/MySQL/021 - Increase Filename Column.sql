--
-- Increase gallery item column size
--
ALTER TABLE `gallery_items` MODIFY COLUMN `title` VARCHAR(500) NOT NULL;
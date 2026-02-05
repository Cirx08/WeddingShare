using System.Data;
using System.Data.Common;
using MySql.Data.MySqlClient;
using WeddingShare.Constants;
using WeddingShare.Enums;
using WeddingShare.Models.Database;

namespace WeddingShare.Helpers.Database
{
    public class MySqlDatabaseHelper : IDatabaseHelper
    {
        private readonly string _connString;
        private readonly ILogger _logger;

        public MySqlDatabaseHelper(IConfigHelper config, ILogger<MySqlDatabaseHelper> logger)
        {
            _connString = config.GetOrDefault(Settings.Database.ConnectionString, "Server=mysql;Port=3306;Database=WeddingShare;Uid=WeddingShare;Pwd=Password;");
            _logger = logger;

            _logger.LogInformation($"Using MySQL connection string");

            this.TestConnection();
        }

        #region Setup
        private async Task<MySqlConnection> GetConnection()
        {
            return await GetConnection(_connString);
        }

        private async Task<MySqlConnection> GetConnection(string connString)
        {
            var conn = new MySqlConnection(connString);
            await DisableOnlyFullGroup(conn);
            return conn;
        }

        private void TestConnection()
        {
            try
            {
                using (var conn = GetConnection().Result)
                {
                    var cmd = CreateCommand($"SHOW TABLES;", conn);
                    cmd.CommandType = CommandType.Text;

                    conn.Open();
                    cmd.ExecuteReader();
                    conn.Close();
                }
            }
            catch (Exception ex)
            {
                var message = $"Failed to connect to the database instance. Please check your database is up and running and the connection string used is correct";
                _logger.LogCritical(ex, message);
                Environment.FailFast(message, ex);
            }
        }

        private MySqlCommand CreateCommand(string cmd, MySqlConnection conn)
        {
            return new MySqlCommand(cmd, conn);
        }

        private async Task<MySqlTransaction> CreateTransaction(MySqlConnection conn)
        {
            return (MySqlTransaction)await conn.BeginTransactionAsync();
        }

        private async Task<bool> DisableOnlyFullGroup(MySqlConnection conn)
        {
            var cmd = CreateCommand($"SET SESSION sql_mode=(SELECT REPLACE(@@sql_mode,'ONLY_FULL_GROUP_BY',''));", conn);
            cmd.CommandType = CommandType.Text;

            await conn.OpenAsync();
            var result = await cmd.ExecuteNonQueryAsync() > 0;
            await conn.CloseAsync();

            return result;
        }

        private void ClearPool(MySqlConnection conn)
        {
            MySqlConnection.ClearPool(conn);
        }
        #endregion

        #region Gallery
        public async Task<int> GetGalleryCount()
        {
            int? result = 0;

            using (var conn = await GetConnection())
            {
                var cmd = CreateCommand($"SELECT COUNT(`id`) AS `count` FROM `galleries`;", conn);
                cmd.CommandType = CommandType.Text;

                await conn.OpenAsync();
                result = (int?)(long?)await cmd.ExecuteScalarAsync();
                await conn.CloseAsync();
            }

            return result ?? 0;
        }
        
        public async Task<IDictionary<string, string>> GetGalleryNames()
        {
            var result = new Dictionary<string, string>();

            using (var conn = await GetConnection())
            {
                var cmd = CreateCommand($"SELECT g.`id`, g.`identifier`, g.`name`, u.`username` AS `owner` FROM `galleries` AS g LEFT JOIN `users` AS u ON g.`owner` = u.`id` ORDER BY g.`name` ASC;", conn);
                cmd.CommandType = CommandType.Text;

                await conn.OpenAsync();
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    if (reader != null && reader.HasRows)
                    {
                        while (reader.Read())
                        {
                            try
                            {
                                var id = !await reader.IsDBNullAsync("id") ? reader.GetInt32("id") : 0;
                                if (id > 0)
                                {
                                    var identifier = !await reader.IsDBNullAsync("identifier") ? reader.GetString("identifier") : null;
                                    var name = !await reader.IsDBNullAsync("name") ? reader.GetString("name") : null;
                                    var owner = !await reader.IsDBNullAsync("owner") ? reader.GetString("owner") : null;
                                    if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(owner))
                                    {
                                        result.Add(identifier, $"{name} ({owner})");
                                    }
                                    else if (!string.IsNullOrWhiteSpace(name))
                                    {
                                        result.Add(identifier, name);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, $"Failed to parse gallery model from database - {ex?.Message}");
                            }
                        }
                    }
                }
                await conn.CloseAsync();
            }

            return result;
        }

        public async Task<List<GalleryModel>> GetAllGalleries()
        {
            List<GalleryModel> result;

            using (var conn = await GetConnection())
            {
                var cmd = CreateCommand($"SELECT g.*, u.`username` AS `owner_name`, COUNT(gi.`id`) AS `total`, SUM(CASE WHEN gi.`state`=@ApprovedState THEN 1 ELSE 0 END) AS `approved`, SUM(CASE WHEN gi.`state`=@PendingState THEN 1 ELSE 0 END) AS `pending`, SUM(gi.file_size) AS `total_gallery_size` FROM `galleries` AS g LEFT JOIN `gallery_items` AS gi ON g.`id` = gi.`gallery_id` LEFT JOIN `users` AS u ON g.`owner` = u.`id` GROUP BY g.`id` ORDER BY `name` ASC;", conn);
                cmd.CommandType = CommandType.Text;
                cmd.Parameters.AddWithValue("PendingState", (int)GalleryItemState.Pending);
                cmd.Parameters.AddWithValue("ApprovedState", (int)GalleryItemState.Approved);

                await conn.OpenAsync();
                using (var reader = await cmd.ExecuteReaderAsync())
                { 
                    result = await ReadGalleries(reader);
                }
                await conn.CloseAsync();
            }

            return result;
        }

        public async Task<List<GalleryModel>> GetUserGalleries(int userId)
        {
            List<GalleryModel> result;

            using (var conn = await GetConnection())
            {
                var cmd = CreateCommand($"SELECT g.*, u.`username` AS `owner_name`, COUNT(gi.`id`) AS `total`, SUM(CASE WHEN gi.`state`=@ApprovedState THEN 1 ELSE 0 END) AS `approved`, SUM(CASE WHEN gi.`state`=@PendingState THEN 1 ELSE 0 END) AS `pending`, SUM(gi.file_size) AS `total_gallery_size` FROM `galleries` AS g LEFT JOIN `gallery_items` AS gi ON g.`id` = gi.`gallery_id` LEFT JOIN `users` AS u ON g.`owner` = u.`id` WHERE g.`owner`=@UserId GROUP BY g.`id` ORDER BY `name` ASC;", conn);
                cmd.CommandType = CommandType.Text;
                cmd.Parameters.AddWithValue("PendingState", (int)GalleryItemState.Pending);
                cmd.Parameters.AddWithValue("ApprovedState", (int)GalleryItemState.Approved);
                cmd.Parameters.AddWithValue("UserId", userId);

                await conn.OpenAsync();
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    result = await ReadGalleries(reader);
                }
                await conn.CloseAsync();
            }

            return result;
        }

        public async Task<int?> GetGalleryIdByName(string? name)
        {
            int? result = null;

            if (!string.IsNullOrWhiteSpace(name))
            {
                using (var conn = await GetConnection())
                {
                    var cmd = CreateCommand($"SELECT g.`id`, u.`username` AS `owner_name` FROM `galleries` AS g LEFT JOIN `users` AS u ON g.`owner` = u.`id` WHERE UPPER(g.`name`)=UPPER(@Name);", conn);
                    cmd.CommandType = CommandType.Text;
                    cmd.Parameters.AddWithValue("Name", name);

                    await conn.OpenAsync();
                    result = (int?)(long?)await cmd.ExecuteScalarAsync();
                    await conn.CloseAsync();
                }
            }

            return result;
        }

        public async Task<int?> GetGalleryId(string identifier)
        {
            int? result = null;

            if (!string.IsNullOrWhiteSpace(identifier))
            {
                if (identifier.Equals("All", StringComparison.OrdinalIgnoreCase))
                {
                    return 0;
                }

                using (var conn = await GetConnection())
                {
                    var cmd = CreateCommand($"SELECT g.`id`, u.`username` AS `owner_name` FROM `galleries` AS g LEFT JOIN `users` AS u ON g.`owner` = u.`id` WHERE UPPER(g.`identifier`)=UPPER(@Identifier);", conn);
                    cmd.CommandType = CommandType.Text;
                    cmd.Parameters.AddWithValue("Identifier", identifier);

                    await conn.OpenAsync();
                    result = (int?)(long?)await cmd.ExecuteScalarAsync();
                    await conn.CloseAsync();
                }
            }

            return result;
        }

        public async Task<string?> GetGalleryIdentifier(int id)
        {
            return (await this.GetGallery(id))?.Identifier;
        }

        public async Task<string?> GetGalleryName(int id)
        {
            return (await this.GetGallery(id))?.Name;
        }

        public async Task<GalleryModel?> GetAllGallery()
        {
            GalleryModel? result;

            using (var conn = await GetConnection())
            {
                var cmd = CreateCommand($"SELECT g.*, u.`username` AS `owner_name`, COUNT(gi.`id`) AS `total`, SUM(CASE WHEN gi.`state`=@ApprovedState THEN 1 ELSE 0 END) AS `approved`, SUM(CASE WHEN gi.`state`=@PendingState THEN 1 ELSE 0 END) AS `pending`, SUM(gi.file_size) AS `total_gallery_size` FROM `galleries` AS g LEFT JOIN `gallery_items` AS gi ON g.`id` = gi.`gallery_id` LEFT JOIN `users` AS u ON g.`owner` = u.`id` ", conn);
                cmd.CommandType = CommandType.Text;
                cmd.Parameters.AddWithValue("PendingState", (int)GalleryItemState.Pending);
                cmd.Parameters.AddWithValue("ApprovedState", (int)GalleryItemState.Approved);

                await conn.OpenAsync();

                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    var galleries = await ReadGalleries(reader);
                    result = new GalleryModel()
                    {
                        Id = 0,
                        Identifier = "all",
                        Name = "all",
                        SecretKey = null,
                        TotalItems = galleries?.Sum(x => x.TotalItems) ?? 0,
                        ApprovedItems = galleries?.Sum(x => x.ApprovedItems) ?? 0,
                        PendingItems = galleries?.Sum(x => x.PendingItems) ?? 0,
                        TotalGallerySize = galleries?.Sum(x => x.TotalGallerySize) ?? 0,
                        Owner = 0,
                        OwnerName = "System"
                    };
                }

                await conn.CloseAsync();
            }

            return result;
        }

        public async Task<GalleryModel?> GetGallery(int id)
        {
            GalleryModel? result;

            if (id == 0)
            {
                return await GetAllGallery();
            }

            using (var conn = await GetConnection())
            {
                var cmd = CreateCommand($"SELECT g.*, u.`username` AS `owner_name`, COUNT(gi.`id`) AS `total`, SUM(CASE WHEN gi.`state`=@ApprovedState THEN 1 ELSE 0 END) AS `approved`, SUM(CASE WHEN gi.`state`=@PendingState THEN 1 ELSE 0 END) AS `pending`, SUM(gi.file_size) AS `total_gallery_size` FROM `galleries` AS g LEFT JOIN `gallery_items` AS gi ON g.`id` = gi.`gallery_id` LEFT JOIN `users` AS u ON g.`owner` = u.`id` WHERE g.`id`=@Id;", conn);
                cmd.CommandType = CommandType.Text;
                cmd.Parameters.AddWithValue("Id", id);
                cmd.Parameters.AddWithValue("PendingState", (int)GalleryItemState.Pending);
                cmd.Parameters.AddWithValue("ApprovedState", (int)GalleryItemState.Approved);

                await conn.OpenAsync();

                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    result = (await ReadGalleries(reader))?.FirstOrDefault();
                }

                await conn.CloseAsync();
            }

            return result;
        }

        public async Task<GalleryModel?> AddGallery(GalleryModel model)
        {
            GalleryModel? result = null;

            using (var conn = await GetConnection())
            {
                var cmd = CreateCommand($"INSERT INTO `galleries` (`identifier`, `name`, `secret_key`, `owner`) VALUES (@Identifier, @Name, @SecretKey, @Owner); SELECT g.*, u.`username` AS `owner_name`, COUNT(gi.`id`) AS `total`, SUM(CASE WHEN gi.`state`=@ApprovedState THEN 1 ELSE 0 END) AS `approved`, SUM(CASE WHEN gi.`state`=@PendingState THEN 1 ELSE 0 END) AS `pending`, SUM(gi.file_size) AS `total_gallery_size` FROM `galleries` AS g LEFT JOIN `gallery_items` AS gi ON g.`id` = gi.`gallery_id` LEFT JOIN `users` AS u ON g.`owner` = u.`id` WHERE g.`id`=LAST_INSERT_ID();", conn);
                cmd.CommandType = CommandType.Text;
                cmd.Parameters.AddWithValue("Identifier", model.Identifier);
                cmd.Parameters.AddWithValue("Name", model.Name.ToLower());
                cmd.Parameters.AddWithValue("SecretKey", !string.IsNullOrWhiteSpace(model.SecretKey) ? model.SecretKey : DBNull.Value);
                cmd.Parameters.AddWithValue("ApprovedState", (int)GalleryItemState.Approved);
                cmd.Parameters.AddWithValue("PendingState", (int)GalleryItemState.Pending);
                cmd.Parameters.AddWithValue("Owner", model.Owner);

                await conn.OpenAsync();
                var tran = await CreateTransaction(conn);

                try
                {
                    cmd.Transaction = tran;
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        result = (await ReadGalleries(reader))?.FirstOrDefault();
                    }
                    await tran.CommitAsync();
                }
                catch
                {
                    await tran.RollbackAsync();
                }

                await conn.CloseAsync();
            }

            return result;
        }

        public async Task<GalleryModel?> EditGallery(GalleryModel model)
        {
            GalleryModel? result = null;

            using (var conn = await GetConnection())
            {
                var cmd = CreateCommand($"UPDATE `galleries` SET `name`=@Name, `secret_key`=@SecretKey, `owner`=@Owner WHERE `id`=@Id; SELECT g.*, u.`username` AS `owner_name`, COUNT(gi.`id`) AS `total`, SUM(CASE WHEN gi.`state`=@ApprovedState THEN 1 ELSE 0 END) AS `approved`, SUM(CASE WHEN gi.`state`=@PendingState THEN 1 ELSE 0 END) AS `pending`, SUM(gi.file_size) AS `total_gallery_size` FROM `galleries` AS g LEFT JOIN `gallery_items` AS gi ON g.`id` = gi.`gallery_id` LEFT JOIN `users` AS u ON g.`owner` = u.`id` WHERE g.`id`=@Id;", conn);
                cmd.CommandType = CommandType.Text;
                cmd.Parameters.AddWithValue("Id", model.Id);
                cmd.Parameters.AddWithValue("Name", model.Name?.ToLower());
                cmd.Parameters.AddWithValue("SecretKey", !string.IsNullOrWhiteSpace(model.SecretKey) ? model.SecretKey : DBNull.Value);
                cmd.Parameters.AddWithValue("ApprovedState", (int)GalleryItemState.Approved);
                cmd.Parameters.AddWithValue("PendingState", (int)GalleryItemState.Pending);
                cmd.Parameters.AddWithValue("Owner", model.Owner);

                await conn.OpenAsync();
                var tran = await CreateTransaction(conn);

                try
                {
                    cmd.Transaction = tran;
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        result = (await ReadGalleries(reader))?.FirstOrDefault();
                    }
                    await tran.CommitAsync();
                }
                catch
                {
                    await tran.RollbackAsync();
                }

                await conn.CloseAsync();
            }

            return result;
        }

        public async Task<bool> WipeGallery(GalleryModel model)
        {
            bool result = false;

            using (var conn = await GetConnection())
            {
                var cmd = CreateCommand($"DELETE FROM `gallery_settings` WHERE `gallery_id`=@Id; DELETE FROM `gallery_items` WHERE `gallery_id`=@Id;", conn);
                cmd.CommandType = CommandType.Text;
                cmd.Parameters.AddWithValue("Id", model.Id);

                await conn.OpenAsync();
                var tran = await CreateTransaction(conn);

                try
                {
                    cmd.Transaction = tran;
                    result = (await cmd.ExecuteNonQueryAsync()) > 0;
                    await tran.CommitAsync();
                }
                catch
                {
                    await tran.RollbackAsync();
                }

                await conn.CloseAsync();
            }

            return result;
        }

        public async Task<bool> WipeAllGalleries()
        {
            bool result = false;

            using (var conn = await GetConnection())
            {
                var cmd = CreateCommand($"DELETE FROM `gallery_settings`; DELETE FROM `gallery_items`; DELETE FROM `galleries` WHERE `id` > 1;", conn);
                cmd.CommandType = CommandType.Text;

                await conn.OpenAsync();
                var tran = await CreateTransaction(conn);

                try
                {
                    cmd.Transaction = tran;
                    result = (await cmd.ExecuteNonQueryAsync()) > 0;
                    await tran.CommitAsync();
                }
                catch
                {
                    await tran.RollbackAsync();
                }

                await conn.CloseAsync();
            }

            return result;
        }

        public async Task<bool> DeleteGallery(GalleryModel model)
        {
            bool result = false;

            using (var conn = await GetConnection())
            {
                var cmd = CreateCommand($"DELETE FROM `gallery_settings` WHERE `gallery_id`=@Id; DELETE FROM `gallery_items` WHERE `gallery_id`=@Id; DELETE FROM `galleries` WHERE `id`=@Id;", conn);
                cmd.CommandType = CommandType.Text;
                cmd.Parameters.AddWithValue("Id", model.Id);

                await conn.OpenAsync();
                var tran = await CreateTransaction(conn);

                try
                {
                    cmd.Transaction = tran;
                    result = (await cmd.ExecuteNonQueryAsync()) > 0;
                    await tran.CommitAsync();
                }
                catch
                {
                    await tran.RollbackAsync();
                }

                await conn.CloseAsync();
            }

            return result;
        }
        #endregion

        #region Gallery Items
        public async Task<IDictionary<string, long>> GetGalleryItemCount(int? galleryId, GalleryItemState state = GalleryItemState.All, MediaType type = MediaType.All, ImageOrientation orientation = ImageOrientation.None)
        {
            var results = new Dictionary<string, long>();

            using (var conn = await GetConnection())
            {
                var query = $"SELECT `state`, COUNT(gi.`id`) AS `count` FROM `gallery_items` AS gi LEFT JOIN `galleries` AS g ON gi.`gallery_id` = g.`id` WHERE {(galleryId != null && galleryId > 0 ? "gi.`gallery_id`=@Id" : "gi.`gallery_id` > 0")}{(type != MediaType.All ? " AND gi.`media_type`=@Type" : string.Empty)}{(orientation != ImageOrientation.None ? " AND gi.`orientation`=@Orientation" : string.Empty)}{(state != GalleryItemState.All ? " AND gi.`state`=@State" : string.Empty)} GROUP BY `state`;";

                var cmd = CreateCommand(query, conn);
                cmd.CommandType = CommandType.Text;
                cmd.Parameters.AddWithValue("Id", galleryId);
                cmd.Parameters.AddWithValue("Type", type);
                cmd.Parameters.AddWithValue("Orientation", orientation);
                cmd.Parameters.AddWithValue("State", state);

                await conn.OpenAsync();
                var reader = await cmd.ExecuteReaderAsync();
                if (reader != null && reader.HasRows)
                {
                    while (reader.Read())
                    {
                        try
                        {
                            var key = !await reader.IsDBNullAsync("state") ? (GalleryItemState)reader.GetInt32("state") : GalleryItemState.Pending;
                            var value = !await reader.IsDBNullAsync("count") ? reader.GetInt64("count") : 0;
                            results.Add(key.ToString(), value);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, $"Failed to parse gallery item model from database - {ex?.Message}");
                        }
                    }
                }

                await conn.CloseAsync();
            }

            foreach (var s in System.Enum.GetNames(typeof(GalleryItemState)))
            {
                if (!results.ContainsKey(s))
                {
                    results.Add(s, s.Equals("All", StringComparison.OrdinalIgnoreCase) ? results.Sum(x => x.Value) : 0);
                }
            }

            return results;
        }

        public async Task<List<GalleryItemModel>> GetAllGalleryItems(int? galleryId, GalleryItemState state = GalleryItemState.All, MediaType type = MediaType.All, ImageOrientation orientation = ImageOrientation.None, GalleryGroup group = GalleryGroup.None, GalleryOrder order = GalleryOrder.Descending, int limit = int.MaxValue, int page = 1)
        {
            List<GalleryItemModel> result;

            using (var conn = await GetConnection())
            {
                var query = $"SELECT g.`name` AS `gallery_name`, gi.* FROM `gallery_items` AS gi LEFT JOIN `galleries` AS g ON gi.`gallery_id` = g.`id` WHERE {(galleryId != null && galleryId > 0 ? "gi.`gallery_id`=@Id" : "gi.`gallery_id` > 0")}{(type != MediaType.All ? " AND gi.`media_type`=@Type" : string.Empty)}{(orientation != ImageOrientation.None ? " AND gi.`orientation`=@Orientation" : string.Empty)}{(state != GalleryItemState.All ? " AND gi.`state`=@State" : string.Empty)};";
                switch (group)
                {
                    case GalleryGroup.Date:
                        query = $"{query.TrimEnd(' ', ';')} ORDER BY gi.`uploaded_date` {(order == GalleryOrder.Ascending ? "ASC" : "DESC")};";
                        break;
                    case GalleryGroup.Uploader:
                        query = $"{query.TrimEnd(' ', ';')} ORDER BY gi.`uploaded_by` {(order == GalleryOrder.Ascending ? "ASC" : "DESC")};";
                        break;
                    case GalleryGroup.MediaType:
                        query = $"{query.TrimEnd(' ', ';')} ORDER BY gi.`media_type` {(order == GalleryOrder.Ascending ? "ASC" : "DESC")};";
                        break;
                    case GalleryGroup.None:
                        switch (order)
                        {
                            case GalleryOrder.Random:
                                query = $"{query.TrimEnd(' ', ';')} ORDER BY RAND();";
                                break;
                            default:
                                query = $"{query.TrimEnd(' ', ';')} ORDER BY gi.`uploaded_date` {(order == GalleryOrder.Ascending ? "ASC" : "DESC")};";
                                break;
                        }
                        break;
                    default:
                        break;
                }

                if (limit > 0 && page > 0)
                {
                    query = $"{query.TrimEnd(' ', ';')} LIMIT @Limit OFFSET @Offset;";
                }

                var cmd = CreateCommand(query, conn);
                cmd.CommandType = CommandType.Text;
                cmd.Parameters.AddWithValue("Id", galleryId);
                cmd.Parameters.AddWithValue("Type", type);
                cmd.Parameters.AddWithValue("Orientation", orientation);
                cmd.Parameters.AddWithValue("State", state);
                cmd.Parameters.AddWithValue("Limit", limit);
                cmd.Parameters.AddWithValue("Offset", ((page - 1) * limit));

                await conn.OpenAsync();
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    result = await ReadGalleryItems(reader);
                }
                await conn.CloseAsync();
            }

            return result;
        }

        public async Task<List<GalleryItemModel>> GetPendingGalleryItems(int? galleryId = null)
        {
            List<GalleryItemModel> result;

            using (var conn = await GetConnection())
            {
                var cmd = CreateCommand($"SELECT g.`name` AS `gallery_name`, gi.* FROM `gallery_items` AS gi LEFT JOIN `galleries` AS g ON g.`id` = gi.`gallery_id` WHERE gi.`state`=@State {(galleryId != null && galleryId > 0 ? "AND gi.`gallery_id`=@GalleryId" : string.Empty)};", conn);
                cmd.CommandType = CommandType.Text;
                cmd.Parameters.AddWithValue("GalleryId", galleryId);
                cmd.Parameters.AddWithValue("State", (int)GalleryItemState.Pending);

                await conn.OpenAsync();
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    result = await ReadPendingGalleryItems(reader);
                }
                await conn.CloseAsync();
            }

            return result;
        }

        public async Task<List<GalleryItemModel>> GetUserPendingGalleryItems(int userId, int? galleryId = null)
        {
            List<GalleryItemModel> result;

            using (var conn = await GetConnection())
            {
                var cmd = CreateCommand($"SELECT g.`name` AS `gallery_name`, gi.* FROM `gallery_items` AS gi LEFT JOIN `galleries` AS g ON g.`id` = gi.`gallery_id` WHERE g.`owner`=@UserId AND gi.`state`=@State {(galleryId != null && galleryId > 0 ? "AND gi.`gallery_id`=@GalleryId" : string.Empty)};", conn);
                cmd.CommandType = CommandType.Text;
                cmd.Parameters.AddWithValue("UserId", userId);
                cmd.Parameters.AddWithValue("GalleryId", galleryId);
                cmd.Parameters.AddWithValue("State", (int)GalleryItemState.Pending);

                await conn.OpenAsync();
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    result = await ReadPendingGalleryItems(reader);
                }
                await conn.CloseAsync();
            }

            return result;
        }

        public async Task<GalleryItemModel?> GetPendingGalleryItem(int id)
        {
            GalleryItemModel? result;

            using (var conn = await GetConnection())
            {
                var cmd = CreateCommand($"SELECT g.`name` AS `gallery_name`, gi.* FROM `gallery_items` AS gi LEFT JOIN `galleries` AS g ON g.`id` = gi.`gallery_id` WHERE gi.`id`=@Id;", conn);
                cmd.CommandType = CommandType.Text;
                cmd.Parameters.AddWithValue("Id", id);

                await conn.OpenAsync();
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    result = (await ReadPendingGalleryItems(reader))?.FirstOrDefault();
                }
                await conn.CloseAsync();
            }

            return result;
        }

        public async Task<long> GetPendingGalleryItemCount(int? galleryId = null)
        {
            long result = 0;

            using (var conn = await GetConnection())
            {
                var cmd = CreateCommand($"SELECT COUNT(`id`) FROM `gallery_items` {(galleryId != null && galleryId > 0 ? "WHERE `gallery_id`=@GalleryId" : string.Empty)};", conn);
                cmd.CommandType = CommandType.Text;
                cmd.Parameters.AddWithValue("GalleryId", galleryId);


                await conn.OpenAsync();
                result = (long)(await cmd.ExecuteScalarAsync() ?? 0);
                await conn.CloseAsync();
            }

            return result;
        }

        public async Task<GalleryItemModel?> GetGalleryItem(int id)
        {
            GalleryItemModel? result;

            using (var conn = await GetConnection())
            {
                var cmd = CreateCommand($"SELECT g.`name` AS `gallery_name`, gi.* FROM `gallery_items` AS gi LEFT JOIN `galleries` AS g ON gi.`gallery_id` = g.`id`  WHERE gi.`id`=@Id;", conn);
                cmd.CommandType = CommandType.Text;
                cmd.Parameters.AddWithValue("Id", id);

                await conn.OpenAsync();
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    result = (await ReadGalleryItems(reader))?.FirstOrDefault();
                }
                await conn.CloseAsync();
            }

            return result;
        }

        public async Task<GalleryItemModel?> GetGalleryItemByChecksum(int galleryId, string checksum)
        {
            GalleryItemModel? result;

            using (var conn = await GetConnection())
            {
                var cmd = CreateCommand($"SELECT g.`name` AS `gallery_name`, gi.* FROM `gallery_items` AS gi LEFT JOIN `galleries` AS g ON gi.`gallery_id` = g.`id`  WHERE g.`id`=@Id AND gi.`checksum`=@Checksum;", conn);
                cmd.CommandType = CommandType.Text;
                cmd.Parameters.AddWithValue("Id", galleryId);
                cmd.Parameters.AddWithValue("Checksum", checksum);

                await conn.OpenAsync();
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    result = (await ReadGalleryItems(reader))?.FirstOrDefault();
                }
                await conn.CloseAsync();
            }

            return result;
        }

        public async Task<GalleryItemModel?> AddGalleryItem(GalleryItemModel model)
        {
            GalleryItemModel? result = null;

            if (model.GalleryId > 0)
            {
                using (var conn = await GetConnection())
                {
                    var cmd = CreateCommand($"INSERT INTO `gallery_items` (`gallery_id`, `title`, `state`, `uploaded_by`, `uploader_email`, `uploaded_date`, `checksum`, `media_type`, `orientation`, `file_size`) VALUES (@GalleryId, @Title, @State, @UploadedBy, @UploaderEmailAddress, @UploadedDate, @Checksum, @MediaType, @Orientation, @FileSize); SELECT g.`name` AS `gallery_name`, gi.* FROM `gallery_items` AS gi LEFT JOIN `galleries` AS g ON gi.`gallery_id` = g.`id` WHERE gi.`id`=LAST_INSERT_ID();", conn);
                    cmd.CommandType = CommandType.Text;
                    cmd.Parameters.AddWithValue("GalleryId", model.GalleryId);
                    cmd.Parameters.AddWithValue("Title", model.Title);
                    cmd.Parameters.AddWithValue("State", (int)model.State);
                    cmd.Parameters.AddWithValue("UploadedBy", !string.IsNullOrWhiteSpace(model.UploadedBy) ? model.UploadedBy : DBNull.Value);
                    cmd.Parameters.AddWithValue("UploaderEmailAddress", !string.IsNullOrWhiteSpace(model.UploaderEmailAddress) ? model.UploaderEmailAddress : DBNull.Value);
                    cmd.Parameters.AddWithValue("UploadedDate", model.UploadedDate ?? DateTime.UtcNow);
                    cmd.Parameters.AddWithValue("Checksum", !string.IsNullOrWhiteSpace(model.Checksum) ? model.Checksum : DBNull.Value);
                    cmd.Parameters.AddWithValue("MediaType", (int)model.MediaType);
                    cmd.Parameters.AddWithValue("Orientation", (int)model.Orientation);
                    cmd.Parameters.AddWithValue("FileSize", model.FileSize);

                    await conn.OpenAsync();
                    var tran = await CreateTransaction(conn);

                    try
                    {
                        cmd.Transaction = tran;
                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            result = (await ReadGalleryItems(reader))?.FirstOrDefault();
                        }
                        await tran.CommitAsync();
                    }
                    catch
                    {
                        await tran.RollbackAsync();
                    }

                    await conn.CloseAsync();
                }
            }

            return result;
        }

        public async Task<GalleryItemModel?> EditGalleryItem(GalleryItemModel model)
        {
            GalleryItemModel? result = null;

            using (var conn = await GetConnection())
            {
                var cmd = CreateCommand($"UPDATE `gallery_items` SET `title`=@Title, `state`=@State, `uploaded_by`=@UploadedBy, `uploader_email`=@UploaderEmailAddress, `uploaded_date`=@UploadedDate, `checksum`=@Checksum, `media_type`=@MediaType, `orientation`=@Orientation, `file_size`=@FileSize WHERE `id`=@Id; SELECT g.`name` AS `gallery_name`, gi.* FROM `gallery_items` AS gi LEFT JOIN `galleries` AS g ON gi.`gallery_id` = g.`id` WHERE gi.`id`=@Id;", conn);
                cmd.CommandType = CommandType.Text;
                cmd.Parameters.AddWithValue("Id", model.Id);
                cmd.Parameters.AddWithValue("Title", model.Title);
                cmd.Parameters.AddWithValue("State", (int)model.State);
                cmd.Parameters.AddWithValue("UploadedBy", !string.IsNullOrWhiteSpace(model.UploadedBy) ? model.UploadedBy : DBNull.Value);
                cmd.Parameters.AddWithValue("UploaderEmailAddress", !string.IsNullOrWhiteSpace(model.UploaderEmailAddress) ? model.UploaderEmailAddress : DBNull.Value);
                cmd.Parameters.AddWithValue("UploadedDate", model.UploadedDate != null ? model.UploadedDate : DBNull.Value);
                cmd.Parameters.AddWithValue("Checksum", !string.IsNullOrWhiteSpace(model.Checksum) ? model.Checksum : DBNull.Value);
                cmd.Parameters.AddWithValue("MediaType", (int)model.MediaType);
                cmd.Parameters.AddWithValue("Orientation", (int)model.Orientation);
                cmd.Parameters.AddWithValue("FileSize", model.FileSize);

                await conn.OpenAsync();
                var tran = await CreateTransaction(conn);

                try
                {
                    cmd.Transaction = tran;
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        result = (await ReadGalleryItems(reader))?.FirstOrDefault();
                    }
                    await tran.CommitAsync();
                }
                catch
                {
                    await tran.RollbackAsync();
                }

                await conn.CloseAsync();
            }

            return result;
        }

        public async Task<bool> DeleteGalleryItem(GalleryItemModel model)
        {
            bool result = false;

            using (var conn = await GetConnection())
            {
                var cmd = CreateCommand($"DELETE FROM `gallery_items` WHERE `id`=@Id;", conn);
                cmd.CommandType = CommandType.Text;
                cmd.Parameters.AddWithValue("Id", model.Id);

                await conn.OpenAsync();
                var tran = await CreateTransaction(conn);

                try
                {
                    cmd.Transaction = tran;
                    result = (await cmd.ExecuteNonQueryAsync()) > 0;
                    await tran.CommitAsync();
                }
                catch
                {
                    await tran.RollbackAsync();
                }

                await conn.CloseAsync();
            }

            return result;
        }
        #endregion

        #region Gallery Item Likes
        public async Task<long> GetGalleryItemLikesCount(int galleryItemId)
        {
            long result = 0;

            using (var conn = await GetConnection())
            {
                var cmd = CreateCommand($"SELECT COUNT(`id`) FROM `gallery_likes` WHERE `gallery_item_id`=@GalleryItemId", conn);
                cmd.CommandType = CommandType.Text;
                cmd.Parameters.AddWithValue("GalleryItemId", galleryItemId);

                await conn.OpenAsync();
                result = (long)(await cmd.ExecuteScalarAsync() ?? 0);
                await conn.CloseAsync();
            }

            return result;
        }

        public async Task<IEnumerable<GalleryItemLikeModel>> GetGalleryItemLikes(int galleryItemId)
        {
            List<GalleryItemLikeModel> result;

            using (var conn = await GetConnection())
            {
                var cmd = CreateCommand($"SELECT * FROM `gallery_likes` WHERE `gallery_item_id`=@GalleryItemId", conn);
                cmd.CommandType = CommandType.Text;
                cmd.Parameters.AddWithValue("GalleryItemId", galleryItemId);

                await conn.OpenAsync();
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    result = await ReadGalleryItemLikes(reader);
                }
                await conn.CloseAsync();
            }

            return result;
        }

        public async Task<IEnumerable<GalleryItemLikeModel>> GetUsersGalleryItemLikes(int userId)
        {
            List<GalleryItemLikeModel> result;

            using (var conn = await GetConnection())
            {
                var cmd = CreateCommand($"SELECT * FROM `gallery_likes` WHERE `user_id`=@UserId", conn);
                cmd.CommandType = CommandType.Text;
                cmd.Parameters.AddWithValue("UserId", userId);

                await conn.OpenAsync();
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    result = await ReadGalleryItemLikes(reader);
                }
                await conn.CloseAsync();
            }

            return result;
        }

        public async Task<bool> CheckUserHasLikedGalleryItem(int galleryItemId, int userId)
        {
            var result = false;

            using (var conn = await GetConnection())
            {
                var cmd = CreateCommand($"SELECT COUNT(`id`) FROM `gallery_likes` WHERE `gallery_item_id`=@GalleryItemId AND `user_id`=@UserId", conn);
                cmd.CommandType = CommandType.Text;
                cmd.Parameters.AddWithValue("GalleryItemId", galleryItemId);
                cmd.Parameters.AddWithValue("UserId", userId);

                await conn.OpenAsync();
                result = (long)(await cmd.ExecuteScalarAsync() ?? 0) > 0;
                await conn.CloseAsync();
            }

            return result;
        }

        public async Task<long> LikeGalleryItem(GalleryItemLikeModel model)
        {
            var liked = await CheckUserHasLikedGalleryItem(model.GalleryItemId, model.UserId);
            if (!liked)
            { 
                using (var conn = await GetConnection())
                {
                    var cmd = CreateCommand($"INSERT INTO `gallery_likes` (`gallery_item_id`, `user_id`, `timestamp`) VALUES (@GalleryItemId, @UserId, @Timestamp)", conn);
                    cmd.CommandType = CommandType.Text;
                    cmd.Parameters.AddWithValue("GalleryItemId", model.GalleryItemId);
                    cmd.Parameters.AddWithValue("UserId", model.UserId);
                    cmd.Parameters.AddWithValue("Timestamp", DateTime.UtcNow);

                    await conn.OpenAsync();
                    await cmd.ExecuteNonQueryAsync();
                    await conn.CloseAsync();
                }
            }

            return await GetGalleryItemLikesCount(model.GalleryItemId);
        }

        public async Task<long> UnLikeGalleryItem(GalleryItemLikeModel model)
        {
            using (var conn = await GetConnection())
            {
                var cmd = CreateCommand($"DELETE FROM `gallery_likes` WHERE `gallery_item_id`=@GalleryItemId AND `user_id`=@UserId", conn);
                cmd.CommandType = CommandType.Text;
                cmd.Parameters.AddWithValue("GalleryItemId", model.GalleryItemId);
                cmd.Parameters.AddWithValue("UserId", model.UserId);

                await conn.OpenAsync();
                await cmd.ExecuteNonQueryAsync();
                await conn.CloseAsync();
            }

            return await GetGalleryItemLikesCount(model.GalleryItemId);
        }
        #endregion

        #region Users
        public async Task<bool> InitOwnerAccount(UserModel model)
        {
            bool result = false;

            using (var conn = await GetConnection())
            {
                var cmd = CreateCommand($"UPDATE `users` SET `username`=@Username, `password`=@Password, `level`=@Level WHERE `id`=@Id;", conn);
                cmd.CommandType = CommandType.Text;
                cmd.Parameters.AddWithValue("Id", 1);
                cmd.Parameters.AddWithValue("Username", model.Username.ToLower());
                cmd.Parameters.AddWithValue("Password", model.Password);
                cmd.Parameters.AddWithValue("Level", (int)UserLevel.Owner);

                await conn.OpenAsync();
                result = await cmd.ExecuteNonQueryAsync() > 0;
                await conn.CloseAsync();
            }

            return result;
        }

        public async Task<bool> ValidateCredentials(string username, string password)
        {
            bool result = false;

            using (var conn = await GetConnection())
            {
                var cmd = CreateCommand($"SELECT COUNT(`id`) FROM `users` WHERE `username`=@Username AND `password`=@Password;", conn);
                cmd.CommandType = CommandType.Text;
                cmd.Parameters.AddWithValue("Id", 1);
                cmd.Parameters.AddWithValue("Username", username.ToLower());
                cmd.Parameters.AddWithValue("Password", password);

                await conn.OpenAsync();
                result = (long)(await cmd.ExecuteScalarAsync() ?? 0) > 0;
                await conn.CloseAsync();
            }

            return result;
        }

        public async Task<List<UserModel>?> GetAllUsers()
        {
            List<UserModel> result = new List<UserModel>();

            using (var conn = await GetConnection())
            {
                var cmd = CreateCommand($"SELECT * FROM `users` ORDER BY UPPER(`username`) ASC;", conn);
                cmd.CommandType = CommandType.Text;

                await conn.OpenAsync();
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    result = (await ReadUsers(reader));
                }
                await conn.CloseAsync();
            }

            return result;
        }

        public async Task<UserModel?> GetUser(int id)
        {
            UserModel? result;

            using (var conn = await GetConnection())
            {
                var cmd = CreateCommand($"SELECT * FROM `users` WHERE `id`=@Id;", conn);
                cmd.CommandType = CommandType.Text;
                cmd.Parameters.AddWithValue("Id", id);

                await conn.OpenAsync();
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    result = (await ReadUsers(reader))?.FirstOrDefault();
                }
                await conn.CloseAsync();
            }

            return result;
        }

        public async Task<UserModel?> GetUserByUsername(string username)
        {
            UserModel? result;

            using (var conn = await GetConnection())
            {
                var cmd = CreateCommand($"SELECT * FROM `users` WHERE `username`=@Username;", conn);
                cmd.CommandType = CommandType.Text;
                cmd.Parameters.AddWithValue("Username", username.ToLower());

                await conn.OpenAsync();
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    result = (await ReadUsers(reader))?.FirstOrDefault();
                }
                await conn.CloseAsync();
            }

            return result;
        }

        public async Task<UserModel?> GetUserByEmail(string email)
        {
            UserModel? result;

            using (var conn = await GetConnection())
            {
                var cmd = CreateCommand($"SELECT * FROM `users` WHERE `email`=@Email;", conn);
                cmd.CommandType = CommandType.Text;
                cmd.Parameters.AddWithValue("Email", email.ToLower());

                await conn.OpenAsync();
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    result = (await ReadUsers(reader))?.FirstOrDefault();
                }
                await conn.CloseAsync();
            }

            return result;
        }

        public async Task<UserModel?> AddUser(UserModel model)
        {
            UserModel? result = null;

            using (var conn = await GetConnection())
            {
                var cmd = CreateCommand($"INSERT INTO `users` (`username`, `email`, `password`, `state`, `level`) VALUES (@Username, @Email, @Password, @State, @Level); SELECT * FROM `users` WHERE `id`=LAST_INSERT_ID();", conn);
                cmd.CommandType = CommandType.Text;
                cmd.Parameters.AddWithValue("Username", model.Username.ToLower());
                cmd.Parameters.AddWithValue("Email", !string.IsNullOrEmpty(model.Email) ? model.Email : DBNull.Value);
                cmd.Parameters.AddWithValue("Password", model.Password);
                cmd.Parameters.AddWithValue("State", (int)model.State);
                cmd.Parameters.AddWithValue("Level", (int)model.Level);

                await conn.OpenAsync();
                var tran = await CreateTransaction(conn);

                try
                {
                    cmd.Transaction = tran;
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        result = (await ReadUsers(reader))?.FirstOrDefault();
                    }
                    await tran.CommitAsync();
                }
                catch
                {
                    await tran.RollbackAsync();
                }

                await conn.CloseAsync();
            }

            return result;
        }

        public async Task<UserModel?> EditUser(UserModel model)
        {
            UserModel? result = null;

            using (var conn = await GetConnection())
            {
                var cmd = CreateCommand($"UPDATE `users` SET `username`=@Username, `email`=@Email, `state`=@State, `level`=@Level, `failed_logins`=@FailedLogins, `lockout_until`=@LockoutUntil WHERE `id`=@Id; SELECT * FROM `users` WHERE `id`=@Id;", conn);
                cmd.CommandType = CommandType.Text;
                cmd.Parameters.AddWithValue("Id", model.Id);
                cmd.Parameters.AddWithValue("Username", model.Username.ToLower());
                cmd.Parameters.AddWithValue("Email", !string.IsNullOrEmpty(model.Email) ? model.Email : DBNull.Value);
                cmd.Parameters.AddWithValue("State", (int)model.State);
                cmd.Parameters.AddWithValue("Level", (int)model.Level);
                cmd.Parameters.AddWithValue("FailedLogins", model.FailedLogins);
                cmd.Parameters.AddWithValue("LockoutUntil", model.LockoutUntil != null ? ((DateTime)model.LockoutUntil - new DateTime(1970, 1, 1)).TotalSeconds : DBNull.Value);

                await conn.OpenAsync();
                var tran = await CreateTransaction(conn);

                try
                {
                    cmd.Transaction = tran;
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        result = (await ReadUsers(reader))?.FirstOrDefault();
                    }
                    await tran.CommitAsync();
                }
                catch
                {
                    await tran.RollbackAsync();
                }

                await conn.CloseAsync();
            }

            return result;
        }

        public async Task<bool> DeleteUser(UserModel model)
        {
            bool result = false;

            if (model.Id > 1)
            {
                using (var conn = await GetConnection())
                {
                    var cmd = CreateCommand($"DELETE FROM `gallery_likes` WHERE `user_id`=@Id; DELETE FROM `users` WHERE `id`=@Id;", conn);
                    cmd.CommandType = CommandType.Text;
                    cmd.Parameters.AddWithValue("Id", model.Id);

                    await conn.OpenAsync();
                    var tran = await CreateTransaction(conn);

                    try
                    {
                        cmd.Transaction = tran;
                        result = (await cmd.ExecuteNonQueryAsync()) > 0;
                        await tran.CommitAsync();
                    }
                    catch
                    {
                        await tran.RollbackAsync();
                    }

                    await conn.CloseAsync();
                }
            }

            return result;
        }

        public async Task<bool> ChangePassword(UserModel model)
        {
            bool result = false;

            using (var conn = await GetConnection())
            {
                var cmd = CreateCommand($"UPDATE `users` SET `password`=@Password WHERE `id`=@Id;", conn);
                cmd.CommandType = CommandType.Text;
                cmd.Parameters.AddWithValue("Id", model.Id);
                cmd.Parameters.AddWithValue("Password", model.Password);

                await conn.OpenAsync();
                var tran = await CreateTransaction(conn);

                try
                {
                    cmd.Transaction = tran;
                    result = (int)(await cmd.ExecuteNonQueryAsync()) > 0;
                    await tran.CommitAsync();
                }
                catch
                {
                    await tran.RollbackAsync();
                }

                await conn.CloseAsync();
            }

            return result;
        }

        public async Task<string> SetUserSecret(int id, string secretCode)
        {
            var result = string.Empty;

            using (var conn = await GetConnection())
            {
                var cmd = CreateCommand($"UPDATE `users` SET `secret_code`=@SecretCode WHERE `id`=@Id;", conn);
                cmd.CommandType = CommandType.Text;
                cmd.Parameters.AddWithValue("Id", id);
                cmd.Parameters.AddWithValue("SecretCode", secretCode);

                await conn.OpenAsync();
                var tran = await CreateTransaction(conn);

                try
                {
                    cmd.Transaction = tran;
                    if (await cmd.ExecuteNonQueryAsync() > 0)
                    {
                        result = secretCode;
                    }
                    await tran.CommitAsync();
                }
                catch
                {
                    await tran.RollbackAsync();
                }

                await conn.CloseAsync();
            }

            return result;
        }

        public async Task<bool> VerifyUserSecret(int id, string secretCode)
        {
            bool result = false;

            using (var conn = await GetConnection())
            {
                var cmd = CreateCommand($"SELECT `secret_code` FROM `users` WHERE `id`=@Id;", conn);
                cmd.CommandType = CommandType.Text;
                cmd.Parameters.AddWithValue("Id", id);

                await conn.OpenAsync();
                result = secretCode.Equals(await cmd.ExecuteScalarAsync());
                await conn.CloseAsync();
            }

            return result;
        }

        public async Task<int> IncrementLockoutCount(int id)
        {
            int result = 0;

            var user = await this.GetUser(id);
            if (user != null)
            {
                user.FailedLogins++;
                result = (await this.EditUser(user))?.FailedLogins ?? 0;
            }

            return result;
        }

        public async Task<bool> SetLockout(int id, DateTime? datetime)
        {
            bool result = false;

            using (var conn = await GetConnection())
            {
                if (datetime != null)
                {
                    var lockout = (DateTime)datetime;
                    datetime = new DateTime(lockout.Year, lockout.Month, lockout.Day, lockout.Hour, lockout.Minute, 0, DateTimeKind.Utc);
                }

                var cmd = CreateCommand($"UPDATE `users` SET `lockout_until`=@LockoutUntil WHERE `id`=@Id; SELECT `lockout_until` FROM `users` WHERE `id`=@Id;", conn);
                cmd.CommandType = CommandType.Text;
                cmd.Parameters.AddWithValue("Id", id);
                cmd.Parameters.AddWithValue("LockoutUntil", datetime != null ? ((DateTime)datetime - new DateTime(1970, 1, 1)).TotalSeconds : DBNull.Value);

                await conn.OpenAsync();
                var tran = await CreateTransaction(conn);

                try
                {
                    cmd.Transaction = tran;
                    using (var reader = await cmd.ExecuteReaderAsync())
                    { 
                        if (reader != null && reader.HasRows)
                        {
                            while (reader.Read())
                            {
                                try
                                {
                                    result = (!await reader.IsDBNullAsync("lockout_until") ? DateTime.UnixEpoch.AddSeconds(reader.GetInt32("lockout_until")) : null) == datetime;
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning(ex, $"Failed to parse user lockout from database - {ex?.Message}");
                                }
                            }
                        }
                    }
                    await tran.CommitAsync();
                }
                catch
                {
                    await tran.RollbackAsync();
                }

                await conn.CloseAsync();
            }

            return result;
        }

        public async Task<bool> ResetLockoutCount(int id)
        {
            bool result = false;

            var user = await this.GetUser(id);
            if (user != null)
            {
                user.FailedLogins = 0;
                result = ((await this.EditUser(user))?.FailedLogins ?? 0) == 0;
            }

            return result;
        }

        public async Task<bool> SetMultiFactorToken(int id, string token)
        {
            bool result = false;

            using (var conn = await GetConnection())
            {
                var cmd = CreateCommand($"UPDATE `users` SET `2fa_token`=@Token WHERE `id`=@Id;", conn);
                cmd.CommandType = CommandType.Text;
                cmd.Parameters.AddWithValue("Id", id);
                cmd.Parameters.AddWithValue("Token", token);

                await conn.OpenAsync();
                var tran = await CreateTransaction(conn);

                try
                {
                    cmd.Transaction = tran;
                    result = await cmd.ExecuteNonQueryAsync() > 0;
                    await tran.CommitAsync();
                }
                catch
                {
                    await tran.RollbackAsync();
                }

                await conn.CloseAsync();
            }

            return result;
        }

        public async Task<bool> ResetMultiFactorToDefault()
        {
            bool result = false;

            using (var conn = await GetConnection())
            {
                var cmd = CreateCommand($"UPDATE `users` SET `2fa_token`='';", conn);
                cmd.CommandType = CommandType.Text;

                await conn.OpenAsync();
                var tran = await CreateTransaction(conn);

                try
                {
                    cmd.Transaction = tran;
                    result = await cmd.ExecuteNonQueryAsync() > 0;
                    await tran.CommitAsync();
                }
                catch
                {
                    await tran.RollbackAsync();
                }

                await conn.CloseAsync();
            }

            return result;
        }
        #endregion

        #region CustomResources
        public async Task<CustomResourceModel?> GetCustomResource(int id)
        {
            CustomResourceModel? result;

            using (var conn = await GetConnection())
            {
                var cmd = CreateCommand($"SELECT * FROM `custom_resources` WHERE `id`=@Id;", conn);
                cmd.CommandType = CommandType.Text;
                cmd.Parameters.AddWithValue("Id", id);

                await conn.OpenAsync();
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    result = (await ReadCustomResources(reader))?.FirstOrDefault();
                }
                await conn.CloseAsync();
            }

            return result;
        }

        public async Task<List<CustomResourceModel>> GetAllCustomResources()
        {
            List<CustomResourceModel> result;

            using (var conn = await GetConnection())
            {
                var cmd = CreateCommand($"SELECT * FROM `custom_resources`;", conn);
                cmd.CommandType = CommandType.Text;

                await conn.OpenAsync();
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    result = (await ReadCustomResources(reader));
                }
                await conn.CloseAsync();
            }

            return result;
        }

        public async Task<List<CustomResourceModel>> GetUserCustomResources(int userId)
        {
            List<CustomResourceModel> result;

            using (var conn = await GetConnection())
            {
                var cmd = CreateCommand($"SELECT * FROM `custom_resources` WHERE `owner`=@UserId;", conn);
                cmd.CommandType = CommandType.Text;
                cmd.Parameters.AddWithValue("UserId", userId);

                await conn.OpenAsync();
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    result = (await ReadCustomResources(reader));
                }
                await conn.CloseAsync();
            }

            return result;
        }

        public async Task<CustomResourceModel?> AddCustomResource(CustomResourceModel model)
        {
            CustomResourceModel? result = null;

            using (var conn = await GetConnection())
            {
                var cmd = CreateCommand($"INSERT INTO `custom_resources` (`file_name`, `uploaded_by`, `owner`) VALUES (@FileName, @UploadedBy, @Owner); SELECT * FROM `custom_resources` WHERE `id`=LAST_INSERT_ID();", conn);
                cmd.CommandType = CommandType.Text;
                cmd.Parameters.AddWithValue("FileName", model.FileName);
                cmd.Parameters.AddWithValue("UploadedBy", !string.IsNullOrEmpty(model.UploadedBy) ? model.UploadedBy : DBNull.Value);
                cmd.Parameters.AddWithValue("Owner", model.Owner);

                await conn.OpenAsync();
                var tran = await CreateTransaction(conn);

                try
                {
                    cmd.Transaction = tran;
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        result = (await ReadCustomResources(reader))?.FirstOrDefault();
                    }
                    await tran.CommitAsync();
                }
                catch
                {
                    await tran.RollbackAsync();
                }

                await conn.CloseAsync();
            }

            return result;
        }

        public async Task<CustomResourceModel?> EditCustomResource(CustomResourceModel model)
        {
            CustomResourceModel? result = null;

            using (var conn = await GetConnection())
            {
                var cmd = CreateCommand($"UPDATE `custom_resource` SET `file_name`=@FileName, `uploaded_by`=@UploadedBy, `owner`=@Owner WHERE `id`=@Id; SELECT * FROM `custom_resources` WHERE `id`=@Id;", conn);
                cmd.CommandType = CommandType.Text;
                cmd.Parameters.AddWithValue("Id", model.Id);
                cmd.Parameters.AddWithValue("FileName", model.FileName);
                cmd.Parameters.AddWithValue("UploadedBy", !string.IsNullOrEmpty(model.UploadedBy) ? model.UploadedBy : DBNull.Value);
                cmd.Parameters.AddWithValue("Owner", model.Owner);

                await conn.OpenAsync();
                var tran = await CreateTransaction(conn);

                try
                {
                    cmd.Transaction = tran;
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        result = (await ReadCustomResources(reader))?.FirstOrDefault();
                    }
                    await tran.CommitAsync();
                }
                catch
                {
                    await tran.RollbackAsync();
                }

                await conn.CloseAsync();
            }

            return result;
        }

        public async Task<bool> DeleteCustomResource(CustomResourceModel model)
        {
            bool result = false;

            using (var conn = await GetConnection())
            {
                var cmd = CreateCommand($"UPDATE `settings` SET `value`='' WHERE `value`=@FileName; DELETE FROM `gallery_settings` WHERE `id`=@BannerImageKey AND `value`=@FileName; DELETE FROM `custom_resources` WHERE `id`=@Id;", conn);
                cmd.CommandType = CommandType.Text;
                cmd.Parameters.AddWithValue("Id", model.Id);
                cmd.Parameters.AddWithValue("BannerImageKey", Settings.Gallery.BannerImage);
                cmd.Parameters.AddWithValue("FileName", $"/custom_resources/{model.FileName}");

                await conn.OpenAsync();
                var tran = await CreateTransaction(conn);

                try
                {
                    cmd.Transaction = tran;
                    result = (await cmd.ExecuteNonQueryAsync()) > 0;
                    await tran.CommitAsync();
                }
                catch
                {
                    await tran.RollbackAsync();
                }

                await conn.CloseAsync();
            }

            return result;
        }
        #endregion

        #region Settings
        public async Task<IEnumerable<SettingModel>?> GetAllSettings(int? galleryId = null)
        {
            List<SettingModel> result = new List<SettingModel>();

            using (var conn = await GetConnection())
            {
                await conn.OpenAsync();

                // Get Default Settings
                var cmd = CreateCommand($"SELECT `id`, `value` FROM `settings`;", conn);
                cmd.CommandType = CommandType.Text;

                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    result = await ReadSettings(reader);
                }

                if (result != null && galleryId != null)
                {
                    // Get Gallery Overrides
                    cmd = CreateCommand($"SELECT `id`, `value` FROM `gallery_settings` WHERE `gallery_id`=@GalleryId;", conn);
                    cmd.Parameters.AddWithValue("GalleryId", (int)galleryId);
                    cmd.CommandType = CommandType.Text;

                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        var overrides = (await ReadSettings(reader))?.Where(x => !string.IsNullOrWhiteSpace(x.Value));
                        if (overrides != null && overrides.Any())
                        {
                            result = result.Where(x => !overrides.Any(o => o.Id.Equals(x.Id, StringComparison.OrdinalIgnoreCase))).ToList();
                            result.AddRange(overrides);
                        }
                    }
                }

                await conn.CloseAsync();
            }

            return result;
        }

        public async Task<SettingModel?> GetSetting(string id)
        {
            return await GetSetting(id, 0);
        }

        public async Task<SettingModel?> GetSetting(string id, string gallery)
        {
            return await GetSetting(id, await this.GetGalleryIdByName(gallery) ?? 0);
        }

        public async Task<SettingModel?> GetSetting(string id, int galleryId)
        {
            SettingModel? result;

            using (var conn = await GetConnection())
            {
                var cmd = CreateCommand($"SELECT `id`, `value` FROM (SELECT `id`, `value`, '2' AS `priority` FROM `settings` WHERE `id`=@Id UNION SELECT `id`, `value`, '1' AS `priority` FROM `gallery_settings` WHERE `id`=@Id AND `gallery_id`=@GalleryId) AS sq1 ORDER BY `priority` ASC LIMIT 1;", conn);

                cmd.Parameters.AddWithValue("Id", id.ToUpper());
                cmd.Parameters.AddWithValue("GalleryId", galleryId);
                cmd.CommandType = CommandType.Text;

                await conn.OpenAsync();
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    result = (await ReadSettings(reader))?.FirstOrDefault();
                }
                await conn.CloseAsync();
            }

            return result;
        }

        public async Task<SettingModel?> GetGallerySpecificSetting(string id, int galleryId)
        {
            SettingModel? result = null;

            using (var conn = await GetConnection())
            {
                var cmd = CreateCommand($"SELECT `id`, `value` FROM `gallery_settings` WHERE `id`=@Id AND `gallery_id`=@GalleryId;", conn);

                cmd.Parameters.AddWithValue("Id", id.ToUpper());
                cmd.Parameters.AddWithValue("GalleryId", galleryId);
                cmd.CommandType = CommandType.Text;

                await conn.OpenAsync();
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    result = (await ReadSettings(reader))?.FirstOrDefault();
                }
                await conn.CloseAsync();
            }

            return result;
        }

        public async Task<SettingModel?> AddSetting(SettingModel model, int? galleryId = null)
        {
            SettingModel? result = null;

            using (var conn = await GetConnection())
            {
                MySqlCommand cmd;
                if (galleryId != null)
                {
                    cmd = CreateCommand($"INSERT INTO `gallery_settings` (`id`, `gallery_id`, `value`) VALUES (@Id, @GalleryId, @Value); SELECT * FROM `gallery_settings` WHERE `id`=LAST_INSERT_ID() AND `gallery_id`=@GalleryId;", conn);
                    cmd.Parameters.AddWithValue("GalleryId", galleryId);
                }
                else
                {
                    cmd = CreateCommand($"INSERT INTO `settings` (`id`, `value`) VALUES (@Id, @Value); SELECT * FROM `settings` WHERE `id`=LAST_INSERT_ID();", conn);
                }

                cmd.Parameters.AddWithValue("Id", model.Id.ToUpper());
                cmd.Parameters.AddWithValue("Value", !string.IsNullOrEmpty(model.Value) ? model.Value : DBNull.Value);
                cmd.CommandType = CommandType.Text;

                await conn.OpenAsync();
                var tran = await CreateTransaction(conn);

                try
                {
                    cmd.Transaction = tran;
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        result = (await ReadSettings(reader))?.FirstOrDefault();
                    }
                    await tran.CommitAsync();
                }
                catch
                {
                    await tran.RollbackAsync();
                }

                await conn.CloseAsync();
            }

            return result;
        }

        public async Task<SettingModel?> EditSetting(SettingModel model, int? galleryId = null)
        {
            SettingModel? result = null;

            using (var conn = await GetConnection())
            {
                MySqlCommand cmd;
                if (galleryId != null)
                {
                    cmd = CreateCommand($"UPDATE `gallery_settings` SET `value`=@Value WHERE `id`=@Id AND `gallery_id`=@GalleryId; SELECT * FROM `gallery_settings` WHERE `id`=@Id AND `gallery_id`=@GalleryId;", conn);
                    cmd.Parameters.AddWithValue("GalleryId", galleryId);
                }
                else
                {
                    cmd = CreateCommand($"UPDATE `settings` SET `value`=@Value WHERE `id`=@Id; SELECT * FROM `settings` WHERE `id`=@Id;", conn);
                }

                cmd.Parameters.AddWithValue("Id", model.Id.ToUpper());
                cmd.Parameters.AddWithValue("Value", !string.IsNullOrEmpty(model.Value) ? model.Value : DBNull.Value);
                cmd.CommandType = CommandType.Text;

                await conn.OpenAsync();
                var tran = await CreateTransaction(conn);

                try
                {
                    cmd.Transaction = tran;
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        result = (await ReadSettings(reader))?.FirstOrDefault();
                    }
                    await tran.CommitAsync();
                }
                catch
                {
                    await tran.RollbackAsync();
                }

                await conn.CloseAsync();
            }

            return result;
        }

        public async Task<SettingModel?> SetSetting(SettingModel model, int? galleryId = null)
        {
            if (!string.IsNullOrWhiteSpace(model.Id))
            {
                try
                {
                    if (galleryId != null)
                    {
                        // Gallery Override
                        var result = await GetGallerySpecificSetting(model.Id, galleryId.Value);
                        if (result == null && !string.IsNullOrEmpty(model.Value))
                        {
                            return await AddSetting(new SettingModel()
                            {
                                Id = model.Id.ToUpper(),
                                Value = model.Value
                            }, galleryId);
                        }
                        else if (result != null && !string.IsNullOrEmpty(model.Value))
                        {
                            return await EditSetting(new SettingModel()
                            {
                                Id = model.Id.ToUpper(),
                                Value = model.Value
                            }, galleryId);
                        }
                        else if (result != null && string.IsNullOrEmpty(model.Value))
                        {
                            await DeleteSetting(new SettingModel()
                            {
                                Id = model.Id.ToUpper(),
                                Value = model.Value
                            }, galleryId);
                        }
                    }
                    else
                    {
                        // Default Setting
                        var result = await GetSetting(model.Id);
                        if (result == null && !string.IsNullOrEmpty(model.Value))
                        {
                            return await AddSetting(new SettingModel()
                            {
                                Id = model.Id.ToUpper(),
                                Value = model.Value
                            });
                        }
                        else if (result != null && !string.IsNullOrEmpty(model.Value))
                        {
                            return await EditSetting(new SettingModel()
                            {
                                Id = model.Id.ToUpper(),
                                Value = model.Value
                            });
                        }
                        else if (result != null && string.IsNullOrEmpty(model.Value))
                        {
                            await DeleteSetting(new SettingModel()
                            {
                                Id = model.Id.ToUpper(),
                                Value = model.Value
                            });
                        }
                    }
                }
                catch { }
            }

            return new SettingModel()
            {
                Id = model.Id.ToUpper(),
                Value = model.Value
            };
        }

        public async Task<bool> DeleteSetting(SettingModel model, int? galleryId = null)
        {
            bool result = false;

            using (var conn = await GetConnection())
            {
                MySqlCommand cmd;
                if (galleryId != null)
                {
                    cmd = CreateCommand($"DELETE FROM `gallery_settings` WHERE `id`=@Id AND `gallery_id`=@GalleryId;", conn);
                    cmd.Parameters.AddWithValue("GalleryId", galleryId);
                }
                else
                {
                    cmd = CreateCommand($"DELETE FROM `gallery_settings` WHERE `id`=@Id; DELETE FROM `settings` WHERE `id`=@Id;", conn);
                }

                cmd.Parameters.AddWithValue("Id", model.Id.ToUpper());
                cmd.CommandType = CommandType.Text;

                await conn.OpenAsync();
                var tran = await CreateTransaction(conn);

                try
                {
                    cmd.Transaction = tran;
                    result = (await cmd.ExecuteNonQueryAsync()) > 0;
                    await tran.CommitAsync();
                }
                catch
                {
                    await tran.RollbackAsync();
                }

                await conn.CloseAsync();
            }

            return result;
        }

        public async Task<bool> DeleteAllSettings(int? galleryId = null)
        {
            bool result = false;

            using (var conn = await GetConnection())
            {
                MySqlCommand cmd;
                if (galleryId != null)
                {
                    cmd = CreateCommand($"DELETE FROM `gallery_settings` WHERE `gallery_id`=@GalleryId;", conn);
                    cmd.Parameters.AddWithValue("GalleryId", galleryId);
                }
                else
                {
                    cmd = CreateCommand($"DELETE FROM `gallery_settings`; DELETE FROM `settings`;", conn);
                }

                cmd.CommandType = CommandType.Text;

                await conn.OpenAsync();
                var tran = await CreateTransaction(conn);

                try
                {
                    cmd.Transaction = tran;
                    result = (await cmd.ExecuteNonQueryAsync()) > 0;
                    await tran.CommitAsync();
                }
                catch
                {
                    await tran.RollbackAsync();
                }

                await conn.CloseAsync();
            }

            return result;
        }
        #endregion

        #region Audit Logs
        public async Task<IEnumerable<AuditLogModel>?> GetAuditLogs(string term = "", int limit = 100)
        {
            List<AuditLogModel> result = new List<AuditLogModel>();

            using (var conn = await GetConnection())
            {
                var cmd = CreateCommand($"SELECT * FROM `audit_logs` WHERE `username` LIKE @Term OR `message` LIKE @Term ORDER BY `id` DESC LIMIT @Limit;", conn);
                cmd.CommandType = CommandType.Text;
                cmd.Parameters.AddWithValue("Term", $"%{term?.Trim()}%");
                cmd.Parameters.AddWithValue("Limit", limit);

                await conn.OpenAsync();
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    result = await ReadAuditLogs(reader);
                }
                await conn.CloseAsync();
            }

            return result;
        }

        public async Task<IEnumerable<AuditLogModel>?> GetUserAuditLogs(int userId, string term = "", int limit = 100)
        {
            List<AuditLogModel> result = new List<AuditLogModel>();

            var username = (await GetUser(userId))?.Username;

            using (var conn = await GetConnection())
            {
                var cmd = CreateCommand($"SELECT * FROM `audit_logs` WHERE `username`=@Username AND `message` LIKE @Term ORDER BY `id` DESC LIMIT @Limit;", conn);
                cmd.CommandType = CommandType.Text;
                cmd.Parameters.AddWithValue("Username", username);
                cmd.Parameters.AddWithValue("Term", $"%{term?.Trim()}%");
                cmd.Parameters.AddWithValue("Limit", limit);

                await conn.OpenAsync();
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    result = await ReadAuditLogs(reader);
                }
                await conn.CloseAsync();
            }

            return result;
        }

        public async Task<AuditLogModel?> AddAuditLog(AuditLogModel model)
        {
            AuditLogModel? result = null;

            using (var conn = await GetConnection())
            {
                var cmd = CreateCommand($"INSERT INTO `audit_logs` (`message`, `username`, `timestamp`) VALUES (@Message, @Username, @Timestamp); SELECT * FROM `audit_logs` WHERE `id`=LAST_INSERT_ID();", conn);
                cmd.Parameters.AddWithValue("Message", model.Message);
                cmd.Parameters.AddWithValue("Username", model.Username?.ToLower());
                cmd.Parameters.AddWithValue("Timestamp", DateTime.UtcNow);
                cmd.CommandType = CommandType.Text;

                await conn.OpenAsync();
                var tran = await CreateTransaction(conn);

                try
                {
                    cmd.Transaction = tran;
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        result = (await ReadAuditLogs(reader))?.FirstOrDefault();
                    }
                    await tran.CommitAsync();
                }
                catch
                {
                    await tran.RollbackAsync();
                }

                await conn.CloseAsync();
            }

            return result;
        }
        #endregion 

        #region Backups
        public async Task<bool> Import(string path)
        {
            bool result = false;

            try
            {
                // Import from SQLite backup file into MySQL
                // path is already a connection string like "Data Source=/path/to/file"
                using (var sqliteConn = new Microsoft.Data.Sqlite.SqliteConnection(path))
                {
                    await sqliteConn.OpenAsync();

                    using (var mysqlConn = await GetConnection())
                    {
                        await mysqlConn.OpenAsync();

                        // Import in order respecting foreign keys
                        await ImportTable(sqliteConn, mysqlConn, "users", 
                            "SELECT id, username, email, password, failed_logins, lockout_until, [2fa_token], state, level, secret_code FROM users",
                            "REPLACE INTO users (id, username, email, password, failed_logins, lockout_until, 2fa_token, state, level, secret_code) VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8, @p9)");
                        
                        await ImportTable(sqliteConn, mysqlConn, "settings",
                            "SELECT id, value FROM settings",
                            "REPLACE INTO settings (id, value) VALUES (@p0, @p1)");
                        
                        await ImportTable(sqliteConn, mysqlConn, "galleries",
                            "SELECT id, identifier, name, secret_key, owner FROM galleries",
                            "REPLACE INTO galleries (id, identifier, name, secret_key, owner) VALUES (@p0, @p1, @p2, @p3, @p4)");
                        
                        await ImportTable(sqliteConn, mysqlConn, "gallery_items",
                            "SELECT id, gallery_id, title, uploaded_by, state, media_type, checksum, orientation, uploaded_date, file_size, uploader_email FROM gallery_items",
                            "REPLACE INTO gallery_items (id, gallery_id, title, uploaded_by, state, media_type, checksum, orientation, uploaded_date, file_size, uploader_email) VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8, @p9, @p10)");
                        
                        await ImportTable(sqliteConn, mysqlConn, "gallery_likes",
                            "SELECT id, gallery_item_id, identifier FROM gallery_likes",
                            "REPLACE INTO gallery_likes (id, gallery_item_id, identifier) VALUES (@p0, @p1, @p2)");
                        
                        await ImportTable(sqliteConn, mysqlConn, "gallery_settings",
                            "SELECT id, gallery_id, value FROM gallery_settings",
                            "REPLACE INTO gallery_settings (id, gallery_id, value) VALUES (@p0, @p1, @p2)");
                        
                        await ImportTable(sqliteConn, mysqlConn, "custom_resources",
                            "SELECT id, type, name, content, owner FROM custom_resources",
                            "REPLACE INTO custom_resources (id, type, name, content, owner) VALUES (@p0, @p1, @p2, @p3, @p4)");
                        
                        await ImportTable(sqliteConn, mysqlConn, "audit_logs",
                            "SELECT id, timestamp, username, action FROM audit_logs",
                            "REPLACE INTO audit_logs (id, timestamp, username, action) VALUES (@p0, @p1, @p2, @p3)");

                        await mysqlConn.CloseAsync();
                    }

                    await sqliteConn.CloseAsync();
                }

                result = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to import database - {ex?.Message}");
            }

            return result;
        }

        private async Task ImportTable(Microsoft.Data.Sqlite.SqliteConnection sqliteConn, MySqlConnection mysqlConn, string tableName, string selectQuery, string insertQuery)
        {
            try
            {
                using (var cmd = new Microsoft.Data.Sqlite.SqliteCommand(selectQuery, sqliteConn))
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        using (var mysqlCmd = CreateCommand(insertQuery, mysqlConn))
                        {
                            for (int i = 0; i < reader.FieldCount; i++)
                            {
                                mysqlCmd.Parameters.AddWithValue($"@p{i}", reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i));
                            }
                            await mysqlCmd.ExecuteNonQueryAsync();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to import table {tableName} - {ex?.Message}");
            }
        }

        public async Task<bool> Export(string path)
        {
            bool result = false;

            try
            {
                // Export MySQL data to SQLite format for compatibility
                // path is already a connection string like "Data Source=/path/to/file"
                using (var sqliteConn = new Microsoft.Data.Sqlite.SqliteConnection(path))
                {
                    await sqliteConn.OpenAsync();

                    // Create SQLite tables - use brackets for column names starting with numbers
                    var createTables = @"
                        CREATE TABLE IF NOT EXISTS galleries (id INTEGER PRIMARY KEY, identifier TEXT, name TEXT NOT NULL UNIQUE, secret_key TEXT, owner INTEGER DEFAULT 0);
                        CREATE TABLE IF NOT EXISTS gallery_items (id INTEGER PRIMARY KEY, gallery_id INTEGER NOT NULL, title TEXT NOT NULL, uploaded_by TEXT, state INTEGER NOT NULL DEFAULT 0, media_type INTEGER DEFAULT 0, checksum TEXT, orientation INTEGER DEFAULT 0, uploaded_date TEXT, file_size INTEGER DEFAULT 0, uploader_email TEXT, FOREIGN KEY (gallery_id) REFERENCES galleries(id));
                        CREATE TABLE IF NOT EXISTS gallery_likes (id INTEGER PRIMARY KEY, gallery_item_id INTEGER NOT NULL, identifier TEXT NOT NULL, FOREIGN KEY (gallery_item_id) REFERENCES gallery_items(id));
                        CREATE TABLE IF NOT EXISTS gallery_settings (id TEXT NOT NULL, gallery_id INTEGER NOT NULL, value TEXT, PRIMARY KEY (id, gallery_id));
                        CREATE TABLE IF NOT EXISTS users (id INTEGER PRIMARY KEY, username TEXT NOT NULL UNIQUE, email TEXT UNIQUE, password TEXT NOT NULL, failed_logins INTEGER DEFAULT 0, lockout_until TEXT, [2fa_token] TEXT, state INTEGER DEFAULT 0, level INTEGER DEFAULT 0, secret_code TEXT);
                        CREATE TABLE IF NOT EXISTS settings (id TEXT PRIMARY KEY, value TEXT);
                        CREATE TABLE IF NOT EXISTS custom_resources (id INTEGER PRIMARY KEY, type INTEGER NOT NULL, name TEXT NOT NULL, content BLOB, owner INTEGER DEFAULT 0);
                        CREATE TABLE IF NOT EXISTS audit_logs (id INTEGER PRIMARY KEY, timestamp TEXT NOT NULL, username TEXT, action TEXT NOT NULL);
                    ";
                    using (var cmd = new Microsoft.Data.Sqlite.SqliteCommand(createTables, sqliteConn))
                    {
                        await cmd.ExecuteNonQueryAsync();
                    }

                    using (var mysqlConn = await GetConnection())
                    {
                        await mysqlConn.OpenAsync();

                        // Export galleries
                        await ExportTable(mysqlConn, sqliteConn, "galleries", "SELECT id, identifier, name, secret_key, owner FROM galleries");
                        
                        // Export gallery_items
                        await ExportTable(mysqlConn, sqliteConn, "gallery_items", "SELECT id, gallery_id, title, uploaded_by, state, media_type, checksum, orientation, uploaded_date, file_size, uploader_email FROM gallery_items");
                        
                        // Export gallery_likes
                        await ExportTable(mysqlConn, sqliteConn, "gallery_likes", "SELECT id, gallery_item_id, identifier FROM gallery_likes");
                        
                        // Export gallery_settings
                        await ExportTable(mysqlConn, sqliteConn, "gallery_settings", "SELECT id, gallery_id, value FROM gallery_settings");
                        
                        // Export users
                        await ExportTable(mysqlConn, sqliteConn, "users", "SELECT id, username, email, password, failed_logins, lockout_until, 2fa_token, state, level, secret_code FROM users");
                        
                        // Export settings
                        await ExportTable(mysqlConn, sqliteConn, "settings", "SELECT id, value FROM settings");
                        
                        // Export custom_resources
                        await ExportTable(mysqlConn, sqliteConn, "custom_resources", "SELECT id, type, name, content, owner FROM custom_resources");
                        
                        // Export audit_logs
                        await ExportTable(mysqlConn, sqliteConn, "audit_logs", "SELECT id, timestamp, username, action FROM audit_logs");

                        await mysqlConn.CloseAsync();
                    }

                    await sqliteConn.CloseAsync();
                }

                result = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to export database - {ex?.Message}");
            }

            return result;
        }

        private async Task ExportTable(MySqlConnection mysqlConn, Microsoft.Data.Sqlite.SqliteConnection sqliteConn, string tableName, string selectQuery)
        {
            try
            {
                using (var cmd = CreateCommand(selectQuery, mysqlConn))
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        var columns = new List<string>();
                        var parameters = new List<string>();
                        var sqliteCmd = new Microsoft.Data.Sqlite.SqliteCommand();
                        sqliteCmd.Connection = sqliteConn;

                        for (int i = 0; i < reader.FieldCount; i++)
                        {
                            var colName = reader.GetName(i);
                            columns.Add($"\"{colName}\"");
                            parameters.Add($"@p{i}");
                            sqliteCmd.Parameters.AddWithValue($"@p{i}", reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i));
                        }

                        sqliteCmd.CommandText = $"INSERT OR REPLACE INTO {tableName} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", parameters)})";
                        await sqliteCmd.ExecuteNonQueryAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to export table {tableName} - {ex?.Message}");
            }
        }
        #endregion

        #region Data Parsers
        private async Task<List<GalleryModel>> ReadGalleries(DbDataReader? reader)
        {
            var items = new List<GalleryModel>();

            if (reader != null && reader.HasRows)
            {
                while (reader.Read())
                {
                    try
                    {
                        var owner = !await reader.IsDBNullAsync("owner") ? reader.GetInt32("owner") : 0;
                        items.Add(new GalleryModel()
                        {
                            Id = !await reader.IsDBNullAsync("id") ? reader.GetInt32("id") : 0,
                            Identifier = !await reader.IsDBNullAsync("identifier") ? reader.GetString("identifier") : GalleryHelper.GenerateGalleryIdentifier(),
                            Name = !await reader.IsDBNullAsync("name") ? reader.GetString("name") : "Unknown",
                            SecretKey = !await reader.IsDBNullAsync("secret_key") ? reader.GetString("secret_key") : null,
                            TotalItems = !await reader.IsDBNullAsync("total") ? reader.GetInt32("total") : 0,
                            ApprovedItems = !await reader.IsDBNullAsync("approved") ? reader.GetInt32("approved") : 0,
                            PendingItems = !await reader.IsDBNullAsync("pending") ? reader.GetInt32("pending") : 0,
                            TotalGallerySize = !await reader.IsDBNullAsync("total_gallery_size") ? reader.GetInt64("total_gallery_size") : 0,
                            Owner = owner,
                            OwnerName = !await reader.IsDBNullAsync("owner_name") ? reader.GetString("owner_name") : owner == 0 ? "System" : "Unknown"
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, $"Failed to parse gallery model from database - {ex?.Message}");
                    }
                }
            }

            return items;
        }

        private async Task<List<GalleryItemModel>> ReadGalleryItems(DbDataReader? reader)
        {
            var items = new List<GalleryItemModel>();

            if (reader != null && reader.HasRows)
            {
                while (reader.Read())
                {
                    try
                    {
                        var id = !await reader.IsDBNullAsync("id") ? reader.GetInt32("id") : 0;
                        if (id > 0)
                        {
                            items.Add(new GalleryItemModel()
                            {
                                Id = id,
                                GalleryId = !await reader.IsDBNullAsync("gallery_id") ? reader.GetInt32("gallery_id") : 0,
                                Title = !await reader.IsDBNullAsync("title") ? reader.GetString("title") : string.Empty,
                                UploadedBy = !await reader.IsDBNullAsync("uploaded_by") ? reader.GetString("uploaded_by") : null,
                                UploaderEmailAddress = !await reader.IsDBNullAsync("uploader_email") ? reader.GetString("uploader_email") : null,
                                UploadedDate = !await reader.IsDBNullAsync("uploaded_date") ? reader.GetDateTime("uploaded_date") : null,
                                Checksum = !await reader.IsDBNullAsync("checksum") ? reader.GetString("checksum") : null,
                                MediaType = !await reader.IsDBNullAsync("media_type") ? (MediaType)reader.GetInt32("media_type") : MediaType.Unknown,
                                Orientation = !await reader.IsDBNullAsync("orientation") ? (ImageOrientation)reader.GetInt32("orientation") : ImageOrientation.None,
                                State = !await reader.IsDBNullAsync("state") ? (GalleryItemState)reader.GetInt32("state") : GalleryItemState.Pending,
                                FileSize = !await reader.IsDBNullAsync("file_size") ? reader.GetInt64("file_size") : 0,
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, $"Failed to parse gallery item model from database - {ex?.Message}");
                    }
                }
            }

            return items;
        }

        private async Task<List<GalleryItemLikeModel>> ReadGalleryItemLikes(DbDataReader? reader)
        {
            var items = new List<GalleryItemLikeModel>();

            if (reader != null && reader.HasRows)
            {
                while (reader.Read())
                {
                    try
                    {
                        var id = !await reader.IsDBNullAsync("id") ? reader.GetInt32("id") : 0;
                        if (id > 0)
                        {
                            items.Add(new GalleryItemLikeModel()
                            {
                                Id = id,
                                GalleryItemId = !await reader.IsDBNullAsync("gallery_item_id") ? reader.GetInt32("gallery_item_id") : 0,
                                UserId = !await reader.IsDBNullAsync("user_id") ? reader.GetInt32("user_id") : 0,
                                Timestamp = !await reader.IsDBNullAsync("timestamp") ? reader.GetDateTime("timestamp") : null
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, $"Failed to parse gallery item like model from database - {ex?.Message}");
                    }
                }
            }

            return items;
        }

        private async Task<List<GalleryItemModel>> ReadPendingGalleryItems(DbDataReader? reader)
        {
            var items = new List<GalleryItemModel>();

            if (reader != null && reader.HasRows)
            {
                while (reader.Read())
                {
                    try
                    {

                        var id = !await reader.IsDBNullAsync("id") ? reader.GetInt32("id") : 0;
                        if (id > 0)
                        {
                            items.Add(new GalleryItemModel()
                            {
                                Id = id,
                                GalleryId = !await reader.IsDBNullAsync("gallery_id") ? reader.GetInt32("gallery_id") : 0,
                                Title = !await reader.IsDBNullAsync("title") ? reader.GetString("title") : string.Empty,
                                UploadedBy = !await reader.IsDBNullAsync("uploaded_by") ? reader.GetString("uploaded_by") : null,
                                UploaderEmailAddress = !await reader.IsDBNullAsync("uploader_email") ? reader.GetString("uploader_email") : null,
                                Checksum = !await reader.IsDBNullAsync("checksum") ? reader.GetString("checksum") : null,
                                MediaType = !await reader.IsDBNullAsync("media_type") ? (MediaType)reader.GetInt32("media_type") : MediaType.Unknown,
                                Orientation = !await reader.IsDBNullAsync("orientation") ? (ImageOrientation)reader.GetInt32("orientation") : ImageOrientation.None,
                                State = !await reader.IsDBNullAsync("state") ? (GalleryItemState)reader.GetInt32("state") : GalleryItemState.Pending,
                                FileSize = !await reader.IsDBNullAsync("file_size") ? reader.GetInt64("file_size") : 0,
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, $"Failed to parse pending gallery item model from database - {ex?.Message}");
                    }
                }
            }

            return items;
        }

        private async Task<List<UserModel>> ReadUsers(DbDataReader? reader)
        {
            var items = new List<UserModel>();

            if (reader != null && reader.HasRows)
            {
                while (reader.Read())
                {
                    try
                    {
                        var id = !await reader.IsDBNullAsync("id") ? reader.GetInt32("id") : 0;
                        if (id > 0)
                        {
                            items.Add(new UserModel()
                            {
                                Id = id,
                                Username = !await reader.IsDBNullAsync("failed_logins") ? reader.GetString("username").ToLower() : string.Empty,
                                Email = !await reader.IsDBNullAsync("email") ? reader.GetString("email") : null,
                                State = !await reader.IsDBNullAsync("state") ? (AccountState)reader.GetInt32("state") : AccountState.Active,
                                Level = !await reader.IsDBNullAsync("level") ? (UserLevel)reader.GetInt32("level") : UserLevel.Free,
                                Password = null,
                                FailedLogins = !await reader.IsDBNullAsync("failed_logins") ? reader.GetInt32("failed_logins") : 0,
                                LockoutUntil = !await reader.IsDBNullAsync("lockout_until") ? DateTime.UnixEpoch.AddSeconds(reader.GetInt32("lockout_until")) : null,
                                MultiFactorToken = !await reader.IsDBNullAsync("2fa_token") ? reader.GetString("2fa_token") : null
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, $"Failed to parse user model from database - {ex?.Message}");
                    }
                }
            }

            return items;
        }

        private async Task<List<CustomResourceModel>> ReadCustomResources(DbDataReader? reader)
        {
            var items = new List<CustomResourceModel>();

            if (reader != null && reader.HasRows)
            {
                while (reader.Read())
                {
                    try
                    {
                        var id = !await reader.IsDBNullAsync("id") ? reader.GetInt32("id") : 0;
                        if (id > 0)
                        {
                            items.Add(new CustomResourceModel()
                            {
                                Id = id,
                                FileName = !await reader.IsDBNullAsync("file_name") ? reader.GetString("file_name") : string.Empty,
                                UploadedBy = !await reader.IsDBNullAsync("uploaded_by") ? reader.GetString("uploaded_by") : null,
                                Owner = !await reader.IsDBNullAsync("owner") ? reader.GetInt32("owner") : 0
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, $"Failed to parse custom resource model from database - {ex?.Message}");
                    }
                }
            }

            return items;
        }

        private async Task<List<SettingModel>> ReadSettings(DbDataReader? reader)
        {
            var items = new List<SettingModel>();

            if (reader != null && reader.HasRows)
            {
                while (reader.Read())
                {
                    try
                    {
                        var id = !await reader.IsDBNullAsync("id") ? reader.GetString("id").ToUpper() : string.Empty;
                        if (!string.IsNullOrWhiteSpace(id))
                        {
                            items.Add(new SettingModel()
                            {
                                Id = id,
                                Value = !await reader.IsDBNullAsync("value") ? reader.GetString("value") : string.Empty
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, $"Failed to parse setting model from database - {ex?.Message}");
                    }
                }
            }

            return items;
        }

        private async Task<List<AuditLogModel>> ReadAuditLogs(DbDataReader? reader)
        {
            var items = new List<AuditLogModel>();

            if (reader != null && reader.HasRows)
            {
                while (reader.Read())
                {
                    try
                    {
                        var id = !await reader.IsDBNullAsync("id") ? reader.GetInt32("id") : 0;
                        if (id > 0)
                        {
                            items.Add(new AuditLogModel()
                            {
                                Id = id,
                                Message = !await reader.IsDBNullAsync("message") ? reader.GetString("message") : string.Empty,
                                Username = !await reader.IsDBNullAsync("username") ? reader.GetString("username") : string.Empty,
                                Timestamp = !await reader.IsDBNullAsync("timestamp") ? reader.GetDateTime("timestamp") : default(DateTime)
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, $"Failed to parse audit log model from database - {ex?.Message}");
                    }
                }
            }

            return items;
        }
        #endregion
    }
}
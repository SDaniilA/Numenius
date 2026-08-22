using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Numenius.Core.Interfaces;
using Numenius.Core.Models;

namespace Numenius.Core.Services
{
    public class DatabaseService : IDatabaseService
    {
        private readonly string _connectionString;
        private readonly Dictionary<string, double> _sourceWeightCache = new();

        public DatabaseService(string dbPath = "numenius.db")
        {
            _connectionString = $"Data Source={dbPath}";
        }

        public async Task InitializeAsync()
        {
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();

            var createMessages = @"
                CREATE TABLE IF NOT EXISTS Messages (
                    Id TEXT PRIMARY KEY,
                    SourceType TEXT,
                    Sender TEXT,
                    RawText TEXT,
                    CleanedText TEXT,
                    ThreatType TEXT,
                    Category INTEGER,
                    Settlements TEXT,
                    Direction TEXT,
                    Status TEXT,
                    IsDuplicate INTEGER,
                    Confidence REAL,
                    ReceivedAt TEXT,
					IncidentId INTEGER
                )";

            var createIncidents = @"
                CREATE TABLE IF NOT EXISTS Incidents (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ThreatType TEXT,
                    Category INTEGER,
                    FirstSeen TEXT,
                    LastSeen TEXT,
                    Status INTEGER,
                    Confidence REAL,
                    ZoneGeoJson TEXT,
                    AffectedSettlements TEXT,
                    AttackWindowStart TEXT,
                    AttackWindowEnd TEXT,
                    IsReconCompleted INTEGER,
                    ReconTime TEXT,
                    Notes TEXT
                )";

            var createPoints = @"
                CREATE TABLE IF NOT EXISTS IncidentPoints (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    IncidentId INTEGER,
                    SettlementName TEXT,
                    Lat REAL,
                    Lon REAL,
                    Time TEXT,
                    FOREIGN KEY(IncidentId) REFERENCES Incidents(Id)
                )";

            var createPredictions = @"
                CREATE TABLE IF NOT EXISTS Predictions (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    IncidentId INTEGER,
                    ZoneGeoJson TEXT,
                    AffectedSettlements TEXT,
                    AttackWindowStart TEXT,
                    AttackWindowEnd TEXT,
                    Confidence REAL,
                    CreatedAt TEXT,
                    Notes TEXT,
                    FOREIGN KEY(IncidentId) REFERENCES Incidents(Id)
                )";
            // Добавляем колонку IncidentId
			try { using var alterCmd = new SqliteCommand("ALTER TABLE Messages ADD COLUMN IncidentId INTEGER", 		conn); await alterCmd.ExecuteNonQueryAsync(); }
			catch { /* колонка уже есть */ }
            // Добавляем колонку PredictorType
            var alterPredictions = @"ALTER TABLE Predictions ADD COLUMN PredictorType TEXT DEFAULT 'Graph';
            ";
            try { using var cmd = new SqliteCommand(alterPredictions, conn); await cmd.ExecuteNonQueryAsync(); }
            catch { /* колонка уже существует */ }

            var createSources = @"
                CREATE TABLE IF NOT EXISTS Sources (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Name TEXT UNIQUE,
                    InitialWeight REAL,
                    CurrentWeight REAL,
                    TotalMessages INTEGER DEFAULT 0,
                    ConfirmedIncidents INTEGER DEFAULT 0,
                    LastUpdate TEXT
                )";

            var createSourceHistory = @"
                CREATE TABLE IF NOT EXISTS SourceWeightHistory (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    SourceId INTEGER,
                    Weight REAL,
                    ChangedAt TEXT,
                    Reason TEXT,
                    FOREIGN KEY(SourceId) REFERENCES Sources(Id)
                )";

            var createSettlements = @"
                CREATE TABLE IF NOT EXISTS Settlements (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Name TEXT UNIQUE,
                    Lat REAL,
                    Lon REAL,
                    NeedsReview INTEGER DEFAULT 0
                )";

            using var cmd1 = new SqliteCommand(createMessages, conn);
            await cmd1.ExecuteNonQueryAsync();
            using var cmd2 = new SqliteCommand(createIncidents, conn);
            await cmd2.ExecuteNonQueryAsync();
            using var cmd3 = new SqliteCommand(createPoints, conn);
            await cmd3.ExecuteNonQueryAsync();
            using var cmd4 = new SqliteCommand(createPredictions, conn);
            await cmd4.ExecuteNonQueryAsync();
            using var cmd5 = new SqliteCommand(createSources, conn);
            await cmd5.ExecuteNonQueryAsync();
            using var cmd6 = new SqliteCommand(createSourceHistory, conn);
            await cmd6.ExecuteNonQueryAsync();
            using var cmd7 = new SqliteCommand(createSettlements, conn);
            await cmd7.ExecuteNonQueryAsync();
        }

        // --- Источники ---

        public async Task<double> GetSourceWeightAsync(string name)
        {
            if (_sourceWeightCache.TryGetValue(name, out double cachedWeight))
                return cachedWeight;

            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            var sql = "SELECT CurrentWeight FROM Sources WHERE Name = @Name";
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Name", name);
            var result = await cmd.ExecuteScalarAsync();
            double weight = result != null ? Convert.ToDouble(result) : 0.5;
            _sourceWeightCache[name] = weight;
            return weight;
        }

        public async Task InitializeSourceAsync(string name, double initialWeight)
        {
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            var sql = @"
                INSERT OR IGNORE INTO Sources (Name, InitialWeight, CurrentWeight, TotalMessages, ConfirmedIncidents, LastUpdate)
                VALUES (@Name, @InitialWeight, @CurrentWeight, 0, 0, @LastUpdate)";
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Name", name);
            cmd.Parameters.AddWithValue("@InitialWeight", initialWeight);
            cmd.Parameters.AddWithValue("@CurrentWeight", initialWeight);
            cmd.Parameters.AddWithValue("@LastUpdate", DateTime.UtcNow.ToString("o"));
            await cmd.ExecuteNonQueryAsync();
            _sourceWeightCache[name] = initialWeight;
        }

        public async Task UpdateSourceStatsAsync(string name, bool confirmed)
        {
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            var selectSql = "SELECT Id, InitialWeight, TotalMessages, ConfirmedIncidents FROM Sources WHERE Name = @Name";
            using var selectCmd = new SqliteCommand(selectSql, conn);
            selectCmd.Parameters.AddWithValue("@Name", name);
            using var reader = await selectCmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                return;
            int id = reader.GetInt32(0);
            double initialWeight = reader.GetDouble(1);
            int totalMessages = reader.GetInt32(2);
            int confirmedIncidents = reader.GetInt32(3);
            reader.Close();

            totalMessages++;
            if (confirmed) confirmedIncidents++;

            double newWeight = (initialWeight + (confirmedIncidents / (double)Math.Max(1, totalMessages))) / 2;
            newWeight = Math.Clamp(newWeight, 0.0, 1.0);

            var updateSql = @"
                UPDATE Sources SET
                    CurrentWeight = @CurrentWeight,
                    TotalMessages = @TotalMessages,
                    ConfirmedIncidents = @ConfirmedIncidents,
                    LastUpdate = @LastUpdate
                WHERE Id = @Id";
            using var updateCmd = new SqliteCommand(updateSql, conn);
            updateCmd.Parameters.AddWithValue("@CurrentWeight", newWeight);
            updateCmd.Parameters.AddWithValue("@TotalMessages", totalMessages);
            updateCmd.Parameters.AddWithValue("@ConfirmedIncidents", confirmedIncidents);
            updateCmd.Parameters.AddWithValue("@LastUpdate", DateTime.UtcNow.ToString("o"));
            updateCmd.Parameters.AddWithValue("@Id", id);
            await updateCmd.ExecuteNonQueryAsync();

            var historySql = @"
                INSERT INTO SourceWeightHistory (SourceId, Weight, ChangedAt, Reason)
                VALUES (@SourceId, @Weight, @ChangedAt, @Reason)";
            using var historyCmd = new SqliteCommand(historySql, conn);
            historyCmd.Parameters.AddWithValue("@SourceId", id);
            historyCmd.Parameters.AddWithValue("@Weight", newWeight);
            historyCmd.Parameters.AddWithValue("@ChangedAt", DateTime.UtcNow.ToString("o"));
            historyCmd.Parameters.AddWithValue("@Reason", "auto_correction");
            await historyCmd.ExecuteNonQueryAsync();

            _sourceWeightCache[name] = newWeight;
        }

        public async Task<Dictionary<string, double>> GetAllSourceWeightsAsync()
        {
            var result = new Dictionary<string, double>();
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            var sql = "SELECT Name, CurrentWeight FROM Sources";
            using var cmd = new SqliteCommand(sql, conn);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                result[reader.GetString(0)] = reader.GetDouble(1);
            }
            return result;
        }

        public async Task ResetSourceWeightAsync(string name, double newWeight, string reason)
        {
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            var sql = @"
                UPDATE Sources SET
                    InitialWeight = @InitialWeight,
                    CurrentWeight = @CurrentWeight,
                    LastUpdate = @LastUpdate
                WHERE Name = @Name";
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@InitialWeight", newWeight);
            cmd.Parameters.AddWithValue("@CurrentWeight", newWeight);
            cmd.Parameters.AddWithValue("@LastUpdate", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("@Name", name);
            await cmd.ExecuteNonQueryAsync();

            var histSql = @"
                INSERT INTO SourceWeightHistory (SourceId, Weight, ChangedAt, Reason)
                SELECT Id, @Weight, @ChangedAt, @Reason FROM Sources WHERE Name = @Name";
            using var histCmd = new SqliteCommand(histSql, conn);
            histCmd.Parameters.AddWithValue("@Weight", newWeight);
            histCmd.Parameters.AddWithValue("@ChangedAt", DateTime.UtcNow.ToString("o"));
            histCmd.Parameters.AddWithValue("@Reason", reason);
            histCmd.Parameters.AddWithValue("@Name", name);
            await histCmd.ExecuteNonQueryAsync();

            _sourceWeightCache[name] = newWeight;
        }

        // --- Сообщения ---

        public async Task SaveRawMessageAsync(RawMessage raw) => await Task.CompletedTask;

        public async Task SaveParsedMessageAsync(ParsedMessage parsed)
        {
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            var sql = @"
                INSERT OR REPLACE INTO Messages (
				Id, SourceType, Sender, RawText, CleanedText, ThreatType, Category,
				Settlements, Direction, Status, IsDuplicate, Confidence, ReceivedAt, IncidentId
			) VALUES (
				@Id, @SourceType, @Sender, @RawText, @CleanedText, @ThreatType, @Category,
				@Settlements, @Direction, @Status, @IsDuplicate, @Confidence, @ReceivedAt, @IncidentId
			)";
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Id", parsed.Id);
            cmd.Parameters.AddWithValue("@SourceType", parsed.SourceType);
            cmd.Parameters.AddWithValue("@Sender", parsed.Sender);
            cmd.Parameters.AddWithValue("@RawText", parsed.CleanedText);
            cmd.Parameters.AddWithValue("@CleanedText", parsed.CleanedText);
            cmd.Parameters.AddWithValue("@ThreatType", parsed.ThreatType);
            cmd.Parameters.AddWithValue("@Category", (int)parsed.Category);
            cmd.Parameters.AddWithValue("@Settlements", string.Join(",", parsed.Settlements.Select(s => s.Name)));
            cmd.Parameters.AddWithValue("@Direction", parsed.Direction ?? "");
            cmd.Parameters.AddWithValue("@Status", parsed.Status);
            cmd.Parameters.AddWithValue("@IsDuplicate", parsed.IsDuplicate ? 1 : 0);
            cmd.Parameters.AddWithValue("@Confidence", parsed.Confidence);
            cmd.Parameters.AddWithValue("@ReceivedAt", parsed.ReceivedAt.ToString("o"));
			cmd.Parameters.AddWithValue("@IncidentId", parsed.IncidentId.HasValue ? parsed.IncidentId.Value : (object)DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }

        // --- Инциденты ---

        public async Task<int> SaveIncidentAsync(Incident incident)
        {
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            var sql = @"
                INSERT INTO Incidents (
                    ThreatType, Category, FirstSeen, LastSeen, Status, Confidence,
                    ZoneGeoJson, AffectedSettlements, AttackWindowStart, AttackWindowEnd,
                    IsReconCompleted, ReconTime, Notes
                ) VALUES (
                    @ThreatType, @Category, @FirstSeen, @LastSeen, @Status, @Confidence,
                    @ZoneGeoJson, @AffectedSettlements, @AttackWindowStart, @AttackWindowEnd,
                    @IsReconCompleted, @ReconTime, @Notes
                );
                SELECT last_insert_rowid();";
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@ThreatType", incident.ThreatType);
            cmd.Parameters.AddWithValue("@Category", (int)incident.Category);
            cmd.Parameters.AddWithValue("@FirstSeen", incident.FirstSeen.ToString("o"));
            cmd.Parameters.AddWithValue("@LastSeen", incident.LastSeen.ToString("o"));
            cmd.Parameters.AddWithValue("@Status", (int)incident.Status);
            cmd.Parameters.AddWithValue("@Confidence", incident.Confidence);
            cmd.Parameters.AddWithValue("@ZoneGeoJson", incident.PredictedZoneGeoJson ?? "");
            cmd.Parameters.AddWithValue("@AffectedSettlements", string.Join(",", incident.AffectedSettlements));
            cmd.Parameters.AddWithValue("@AttackWindowStart", incident.AttackWindowStart?.ToString("o") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@AttackWindowEnd", incident.AttackWindowEnd?.ToString("o") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@IsReconCompleted", incident.IsReconCompleted ? 1 : 0);
            cmd.Parameters.AddWithValue("@ReconTime", incident.ReconTime?.ToString("o") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@Notes", incident.Notes);
            var id = (long)await cmd.ExecuteScalarAsync();
            incident.Id = (int)id;

            foreach (var pt in incident.Points)
            {
                var ptSql = @"
                    INSERT INTO IncidentPoints (IncidentId, SettlementName, Lat, Lon, Time)
                    VALUES (@IncidentId, @SettlementName, @Lat, @Lon, @Time)";
                using var ptCmd = new SqliteCommand(ptSql, conn);
                ptCmd.Parameters.AddWithValue("@IncidentId", incident.Id);
                ptCmd.Parameters.AddWithValue("@SettlementName", pt.SettlementName);
                ptCmd.Parameters.AddWithValue("@Lat", pt.Lat);
                ptCmd.Parameters.AddWithValue("@Lon", pt.Lon);
                ptCmd.Parameters.AddWithValue("@Time", pt.Time.ToString("o"));
                await ptCmd.ExecuteNonQueryAsync();
            }

            return incident.Id;
        }

        public async Task UpdateIncidentAsync(Incident incident)
        {
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            var sql = @"
                UPDATE Incidents SET
                    ThreatType = @ThreatType,
                    Category = @Category,
                    LastSeen = @LastSeen,
                    Status = @Status,
                    Confidence = @Confidence,
                    ZoneGeoJson = @ZoneGeoJson,
                    AffectedSettlements = @AffectedSettlements,
                    AttackWindowStart = @AttackWindowStart,
                    AttackWindowEnd = @AttackWindowEnd,
                    IsReconCompleted = @IsReconCompleted,
                    ReconTime = @ReconTime,
                    Notes = @Notes
                WHERE Id = @Id";
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Id", incident.Id);
            cmd.Parameters.AddWithValue("@ThreatType", incident.ThreatType);
            cmd.Parameters.AddWithValue("@Category", (int)incident.Category);
            cmd.Parameters.AddWithValue("@LastSeen", incident.LastSeen.ToString("o"));
            cmd.Parameters.AddWithValue("@Status", (int)incident.Status);
            cmd.Parameters.AddWithValue("@Confidence", incident.Confidence);
            cmd.Parameters.AddWithValue("@ZoneGeoJson", incident.PredictedZoneGeoJson ?? "");
            cmd.Parameters.AddWithValue("@AffectedSettlements", string.Join(",", incident.AffectedSettlements));
            cmd.Parameters.AddWithValue("@AttackWindowStart", incident.AttackWindowStart?.ToString("o") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@AttackWindowEnd", incident.AttackWindowEnd?.ToString("o") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@IsReconCompleted", incident.IsReconCompleted ? 1 : 0);
            cmd.Parameters.AddWithValue("@ReconTime", incident.ReconTime?.ToString("o") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@Notes", incident.Notes);
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task<IEnumerable<Incident>> GetActiveIncidentsAsync()
        {
            var result = new List<Incident>();
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            var sql = "SELECT * FROM Incidents WHERE Status IN (0, 1)";
            using var cmd = new SqliteCommand(sql, conn);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var inc = new Incident
                {
                    Id = reader.GetInt32(0),
                    ThreatType = reader.GetString(1),
                    Category = (ThreatCategory)reader.GetInt32(2),
                    FirstSeen = DateTime.Parse(reader.GetString(3)),
                    LastSeen = DateTime.Parse(reader.GetString(4)),
                    Status = (IncidentStatus)reader.GetInt32(5),
                    Confidence = reader.GetDouble(6),
                    PredictedZoneGeoJson = reader.GetString(7),
                    AffectedSettlements = reader.GetString(8).Split(',', StringSplitOptions.RemoveEmptyEntries).ToList(),
                    AttackWindowStart = reader.IsDBNull(9) ? null : DateTime.Parse(reader.GetString(9)),
                    AttackWindowEnd = reader.IsDBNull(10) ? null : DateTime.Parse(reader.GetString(10)),
                    IsReconCompleted = reader.GetInt32(11) == 1,
                    ReconTime = reader.IsDBNull(12) ? null : DateTime.Parse(reader.GetString(12)),
                    Notes = reader.GetString(13)
                };
                var ptSql = "SELECT SettlementName, Lat, Lon, Time FROM IncidentPoints WHERE IncidentId = @Id";
                using var ptCmd = new SqliteCommand(ptSql, conn);
                ptCmd.Parameters.AddWithValue("@Id", inc.Id);
                using var ptReader = await ptCmd.ExecuteReaderAsync();
                while (await ptReader.ReadAsync())
                {
                    inc.Points.Add(new IncidentPoint
                    {
                        SettlementName = ptReader.GetString(0),
                        Lat = ptReader.GetDouble(1),
                        Lon = ptReader.GetDouble(2),
                        Time = DateTime.Parse(ptReader.GetString(3))
                    });
                }
                result.Add(inc);
            }
            return result;
        }

        public async Task<IEnumerable<Incident>> GetAllIncidentsAsync(int maxAgeDays)
        {
            var result = new List<Incident>();
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            var cutoff = DateTime.UtcNow.AddDays(-maxAgeDays);
            var sql = "SELECT * FROM Incidents WHERE FirstSeen >= @Cutoff";
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Cutoff", cutoff.ToString("o"));
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var inc = new Incident
                {
                    Id = reader.GetInt32(0),
                    ThreatType = reader.GetString(1),
                    Category = (ThreatCategory)reader.GetInt32(2),
                    FirstSeen = DateTime.Parse(reader.GetString(3)),
                    LastSeen = DateTime.Parse(reader.GetString(4)),
                    Status = (IncidentStatus)reader.GetInt32(5),
                    Confidence = reader.GetDouble(6),
                    PredictedZoneGeoJson = reader.GetString(7),
                    AffectedSettlements = reader.GetString(8).Split(',', StringSplitOptions.RemoveEmptyEntries).ToList(),
                    AttackWindowStart = reader.IsDBNull(9) ? null : DateTime.Parse(reader.GetString(9)),
                    AttackWindowEnd = reader.IsDBNull(10) ? null : DateTime.Parse(reader.GetString(10)),
                    IsReconCompleted = reader.GetInt32(11) == 1,
                    ReconTime = reader.IsDBNull(12) ? null : DateTime.Parse(reader.GetString(12)),
                    Notes = reader.GetString(13)
                };
                var ptSql = "SELECT SettlementName, Lat, Lon, Time FROM IncidentPoints WHERE IncidentId = @Id";
                using var ptCmd = new SqliteCommand(ptSql, conn);
                ptCmd.Parameters.AddWithValue("@Id", inc.Id);
                using var ptReader = await ptCmd.ExecuteReaderAsync();
                while (await ptReader.ReadAsync())
                {
                    inc.Points.Add(new IncidentPoint
                    {
                        SettlementName = ptReader.GetString(0),
                        Lat = ptReader.GetDouble(1),
                        Lon = ptReader.GetDouble(2),
                        Time = DateTime.Parse(ptReader.GetString(3))
                    });
                }
                result.Add(inc);
            }
            return result;
        }

        public async Task<IEnumerable<Incident>> GetIncidentsForPeriodAsync(DateTime start, DateTime end)
        {
            var result = new List<Incident>();
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            var sql = "SELECT * FROM Incidents WHERE FirstSeen >= @Start AND FirstSeen <= @End";
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Start", start.ToString("o"));
            cmd.Parameters.AddWithValue("@End", end.ToString("o"));
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var inc = MapIncident(reader);
                inc.Points = (await GetPointsForIncident(inc.Id)).ToList();
                result.Add(inc);
            }
            return result;
        }

        private Incident MapIncident(SqliteDataReader reader)
        {
            return new Incident
            {
                Id = reader.GetInt32(0),
                ThreatType = reader.GetString(1),
                Category = (ThreatCategory)reader.GetInt32(2),
                FirstSeen = DateTime.Parse(reader.GetString(3)),
                LastSeen = DateTime.Parse(reader.GetString(4)),
                Status = (IncidentStatus)reader.GetInt32(5),
                Confidence = reader.GetDouble(6),
                PredictedZoneGeoJson = reader.GetString(7),
                AffectedSettlements = reader.GetString(8).Split(',', StringSplitOptions.RemoveEmptyEntries).ToList(),
                AttackWindowStart = reader.IsDBNull(9) ? null : DateTime.Parse(reader.GetString(9)),
                AttackWindowEnd = reader.IsDBNull(10) ? null : DateTime.Parse(reader.GetString(10)),
                IsReconCompleted = reader.GetInt32(11) == 1,
                ReconTime = reader.IsDBNull(12) ? null : DateTime.Parse(reader.GetString(12)),
                Notes = reader.GetString(13)
            };
        }

        private async Task<IEnumerable<IncidentPoint>> GetPointsForIncident(int incidentId)
        {
            var result = new List<IncidentPoint>();
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            var sql = "SELECT SettlementName, Lat, Lon, Time FROM IncidentPoints WHERE IncidentId = @Id ORDER BY Time";
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Id", incidentId);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                result.Add(new IncidentPoint
                {
                    SettlementName = reader.GetString(0),
                    Lat = reader.GetDouble(1),
                    Lon = reader.GetDouble(2),
                    Time = DateTime.Parse(reader.GetString(3))
                });
            }
            return result;
        }

        // --- Прогнозы ---

        public async Task SavePredictionAsync(Prediction prediction, string predictorType = "Graph")
        {
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            var sql = @"
                INSERT INTO Predictions (
                    IncidentId, ZoneGeoJson, AffectedSettlements,
                    AttackWindowStart, AttackWindowEnd, Confidence, CreatedAt, Notes, PredictorType
                ) VALUES (
                    @IncidentId, @ZoneGeoJson, @AffectedSettlements,
                    @AttackWindowStart, @AttackWindowEnd, @Confidence, @CreatedAt, @Notes, @PredictorType
                )";
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@IncidentId", prediction.IncidentId);
            cmd.Parameters.AddWithValue("@ZoneGeoJson", prediction.ZoneGeoJson ?? "");
            cmd.Parameters.AddWithValue("@AffectedSettlements", string.Join(",", prediction.AffectedSettlements));
            cmd.Parameters.AddWithValue("@AttackWindowStart", prediction.AttackWindowStart?.ToString("o") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@AttackWindowEnd", prediction.AttackWindowEnd?.ToString("o") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@Confidence", prediction.Confidence);
            cmd.Parameters.AddWithValue("@CreatedAt", prediction.CreatedAt.ToString("o"));
            cmd.Parameters.AddWithValue("@Notes", prediction.Notes);
            cmd.Parameters.AddWithValue("@PredictorType", predictorType);
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task<IEnumerable<Prediction>> GetPredictionsForIncidentAsync(int incidentId)
        {
            var result = new List<Prediction>();
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            var sql = "SELECT * FROM Predictions WHERE IncidentId = @Id ORDER BY CreatedAt DESC";
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Id", incidentId);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                result.Add(new Prediction
                {
                    Id = reader.GetInt32(0),
                    IncidentId = reader.GetInt32(1),
                    ZoneGeoJson = reader.GetString(2),
                    AffectedSettlements = reader.GetString(3).Split(',', StringSplitOptions.RemoveEmptyEntries).ToList(),
                    AttackWindowStart = reader.IsDBNull(4) ? null : DateTime.Parse(reader.GetString(4)),
                    AttackWindowEnd = reader.IsDBNull(5) ? null : DateTime.Parse(reader.GetString(5)),
                    Confidence = reader.GetDouble(6),
                    CreatedAt = DateTime.Parse(reader.GetString(7)),
                    Notes = reader.GetString(8),
                    PredictorType = reader.IsDBNull(9) ? "Unknown" : reader.GetString(9)
                });
            }
            return result;
        }

		public async Task<IEnumerable<Prediction>> GetPredictionsForPeriodAsync(DateTime start, DateTime end)
		{
			var result = new List<Prediction>();
			using var conn = new SqliteConnection(_connectionString);
			await conn.OpenAsync();
			var sql = @"
				SELECT p.* FROM Predictions p
				JOIN Incidents i ON p.IncidentId = i.Id
				WHERE i.FirstSeen >= @Start AND i.FirstSeen <= @End
				ORDER BY p.CreatedAt DESC";
			using var cmd = new SqliteCommand(sql, conn);
			cmd.Parameters.AddWithValue("@Start", start.ToString("o"));
			cmd.Parameters.AddWithValue("@End", end.ToString("o"));
			using var reader = await cmd.ExecuteReaderAsync();
			while (await reader.ReadAsync())
			{
				result.Add(new Prediction
				{
					Id = reader.GetInt32(0),
					IncidentId = reader.GetInt32(1),
					ZoneGeoJson = reader.GetString(2),
					AffectedSettlements = reader.GetString(3).Split(',', StringSplitOptions.RemoveEmptyEntries).ToList(),
					AttackWindowStart = reader.IsDBNull(4) ? null : DateTime.Parse(reader.GetString(4)),
					AttackWindowEnd = reader.IsDBNull(5) ? null : DateTime.Parse(reader.GetString(5)),
					Confidence = reader.GetDouble(6),
					CreatedAt = DateTime.Parse(reader.GetString(7)),
					Notes = reader.GetString(8),
					PredictorType = reader.IsDBNull(9) ? "Unknown" : reader.GetString(9)
				});
			}
			return result;
		}

        // --- Координаты ---

        public async Task SaveSettlementAsync(Settlement settlement)
        {
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            var sql = @"
                INSERT OR REPLACE INTO Settlements (Name, Lat, Lon, NeedsReview)
                VALUES (@Name, @Lat, @Lon, @NeedsReview)";
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Name", settlement.Name);
            cmd.Parameters.AddWithValue("@Lat", settlement.Lat);
            cmd.Parameters.AddWithValue("@Lon", settlement.Lon);
            cmd.Parameters.AddWithValue("@NeedsReview", settlement.NeedsReview ? 1 : 0);
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task<IEnumerable<Settlement>> GetAllSettlementsAsync()
        {
            var result = new List<Settlement>();
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            var sql = "SELECT Name, Lat, Lon, NeedsReview FROM Settlements";
            using var cmd = new SqliteCommand(sql, conn);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                result.Add(new Settlement
                {
                    Name = reader.GetString(0),
                    Lat = reader.GetDouble(1),
                    Lon = reader.GetDouble(2),
                    NeedsReview = reader.GetInt32(3) == 1
                });
            }
            return result;
        }
		
		public async Task CloseOldIncidentsAsync()
		{
			using var conn = new SqliteConnection(_connectionString);
			await conn.OpenAsync();
			using var transaction = await conn.BeginTransactionAsync();

			var active = await GetActiveIncidentsAsync();
			int closed = 0;

			foreach (var inc in active)
			{
				var start = inc.FirstSeen.AddHours(-1);
				var end = inc.LastSeen.AddHours(1);
				var firstSettlement = inc.AffectedSettlements.FirstOrDefault();
				if (string.IsNullOrEmpty(firstSettlement))
					continue;

				var sql = @"
					SELECT m.Id, m.ReceivedAt, m.Settlements
					FROM Messages m
					WHERE m.Status = 'Terminated'
					  AND m.ReceivedAt >= @Start
					  AND m.ReceivedAt <= @End
					  AND (
						  m.Settlements LIKE @Settlement1
						  OR m.Settlements LIKE @Settlement2
					  )
					ORDER BY m.ReceivedAt DESC
					LIMIT 1";
				using var cmd = new SqliteCommand(sql, conn, (SqliteTransaction)transaction);
				cmd.Parameters.AddWithValue("@Start", start.ToString("o"));
				cmd.Parameters.AddWithValue("@End", end.ToString("o"));
				cmd.Parameters.AddWithValue("@Settlement1", $"%{firstSettlement}%");
				cmd.Parameters.AddWithValue("@Settlement2", $"%{firstSettlement}%");

				using var reader = await cmd.ExecuteReaderAsync();
				if (await reader.ReadAsync())
				{
					var updateSql = @"
						UPDATE Incidents
						SET Status = @Status, Notes = @Notes
						WHERE Id = @Id";
					using var updateCmd = new SqliteCommand(updateSql, conn, (SqliteTransaction)transaction);
					updateCmd.Parameters.AddWithValue("@Status", (int)IncidentStatus.Terminated);
					updateCmd.Parameters.AddWithValue("@Notes", inc.Notes + " Закрыт автоматически по старому отбою");
					updateCmd.Parameters.AddWithValue("@Id", inc.Id);
					await updateCmd.ExecuteNonQueryAsync();
					closed++;
				}
			}

			await transaction.CommitAsync();

			if (closed > 0)
				Console.WriteLine($"🧠 Закрыто {closed} старых инцидентов (по отбоям).");
		}
		
		public async Task<ParsedMessage?> GetLastParsedMessageForIncidentAsync(int incidentId)
		{
			using var conn = new SqliteConnection(_connectionString);
			await conn.OpenAsync();
			var sql = @"
				SELECT Id, SourceType, Sender, ReceivedAt, ThreatType, Category, Settlements, Direction, Status, Confidence, CleanedText, IncidentId
				FROM Messages
				WHERE IncidentId = @IncidentId
				ORDER BY ReceivedAt DESC
				LIMIT 1";
			using var cmd = new SqliteCommand(sql, conn);
			cmd.Parameters.AddWithValue("@IncidentId", incidentId);
			using var reader = await cmd.ExecuteReaderAsync();
			if (await reader.ReadAsync())
			{
				return new ParsedMessage
				{
					Id = reader.GetString(0),
					SourceType = reader.GetString(1),
					Sender = reader.GetString(2),
					ReceivedAt = DateTime.Parse(reader.GetString(3)),
					ThreatType = reader.GetString(4),
					Category = (ThreatCategory)reader.GetInt32(5),
					Settlements = reader.GetString(6).Split(',', StringSplitOptions.RemoveEmptyEntries)
						.Select(n => new Settlement { Name = n }).ToList(),
					Direction = reader.IsDBNull(7) ? null : reader.GetString(7),
					Status = reader.GetString(8),
					Confidence = reader.GetDouble(9),
					CleanedText = reader.GetString(10),
					IncidentId = reader.IsDBNull(11) ? null : reader.GetInt32(11)
				};
			}
			return null;
		}
		
    }
}
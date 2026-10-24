using System.Data;
using System.Text.RegularExpressions;
using ArgumentMap.Contracts;
using MySqlConnector;

namespace ArgumentMap.Storage;

/// <summary>Load, validate and commit under one InnoDB transaction and a shared metadata row lock.</summary>
public sealed partial class MySqlLineRepository : ILineRepository
{
    public const int CurrentSchemaVersion = 35;
    private readonly string connectionString;
    public string DatabaseName { get; }

    public MySqlLineRepository(MySqlSettings settings)
    {
        if (!Regex.IsMatch(settings.Database, "^[A-Za-z][A-Za-z0-9_]{0,63}$"))
            throw new ArgumentException("数据库名称格式无效。");
        DatabaseName = settings.Database;
        connectionString = settings.ConnectionString();
        using (var connection = new MySqlConnection(settings.ConnectionString(includeDatabase: false)))
        {
            connection.Open();
            using var create = connection.CreateCommand();
            create.CommandText = $"CREATE DATABASE IF NOT EXISTS `{DatabaseName}` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci";
            create.ExecuteNonQuery();
        }
        using var database = Open();
        InitializeSchema(database);
    }

    private void InitializeSchema(MySqlConnection database)
    {
        var lockName = DatabaseName + ":Schema";
        using var acquire = database.CreateCommand();
        acquire.CommandText = "SELECT GET_LOCK(@name,15)";
        acquire.Parameters.AddWithValue("@name", lockName);
        if (Convert.ToInt32(acquire.ExecuteScalar()) != 1) throw new InvalidOperationException("数据库架构升级正在进行，请稍后重试。");
        try
        {
        var coded = TableExists(database, "Lines") && ColumnExists(database, "Lines", "LineCode");
        RunSchema(database, coded || !TableExists(database, "MapMetadata") ? "Schema.sql" : "Schema.Legacy.sql");
        using var version = database.CreateCommand();
        version.CommandText = "SELECT SchemaVersion FROM MapMetadata WHERE Id=1";
        var currentVersion = Convert.ToInt32(version.ExecuteScalar());
        if(currentVersion is LogicalNodeInstanceAttributesPending or LogicalNodeInstanceAttributeSetsPending or LogicalNodeInstanceAliasPending or LogicalNodeInstanceConjunctionPending)
        {UpgradeLogicalNodeInstanceAttributes(database);EnsureDefaultSpeed(database);return;}
        if(currentVersion==LogicalNodeOperandPending)
        {UpgradeLogicalNodeOperandKind(database);EnsureDefaultSpeed(database);return;}
        if(currentVersion==LogicalNodeEquivalencePending)
        {UpgradeLogicalNodeEquivalence(database);EnsureDefaultSpeed(database);return;}
        if(currentVersion==LogicalNodeSetRelationsPending)
        {UpgradeLogicalNodeSetRelations(database);UpgradeLogicalNodeEquivalence(database);EnsureDefaultSpeed(database);return;}
        if(currentVersion==LogicalNodeInstancesPending)
        {UpgradeLogicalNodeInstances(database);UpgradeLogicalNodeSetRelations(database);UpgradeLogicalNodeEquivalence(database);EnsureDefaultSpeed(database);return;}
        if(currentVersion==LogicalNodeCoordinatesPending)
        {UpgradeLogicalNodeCoordinates(database);UpgradeLogicalNodeInstances(database);UpgradeLogicalNodeSetRelations(database);UpgradeLogicalNodeEquivalence(database);EnsureDefaultSpeed(database);return;}
        if(currentVersion==LogicalNodeRelationLogicPending)
        {UpgradeLogicalNodeRelationLogic(database);UpgradeLogicalNodeCoordinates(database);UpgradeLogicalNodeInstances(database);UpgradeLogicalNodeSetRelations(database);UpgradeLogicalNodeEquivalence(database);EnsureDefaultSpeed(database);return;}
        if(currentVersion==LogicalNode12LogicPending)
        {UpgradeLogicalNode12Logic(database);UpgradeLogicalNodeRelations(database);UpgradeLogicalNodeRelationLogic(database);UpgradeLogicalNodeCoordinates(database);UpgradeLogicalNodeInstances(database);UpgradeLogicalNodeSetRelations(database);UpgradeLogicalNodeEquivalence(database);EnsureDefaultSpeed(database);return;}
        if(currentVersion==LogicalNodeBranchPending)
        {UpgradeLogicalNodeBranches(database);UpgradeLogicalNode12Logic(database);UpgradeLogicalNodeRelations(database);UpgradeLogicalNodeRelationLogic(database);UpgradeLogicalNodeCoordinates(database);UpgradeLogicalNodeInstances(database);UpgradeLogicalNodeSetRelations(database);UpgradeLogicalNodeEquivalence(database);EnsureDefaultSpeed(database);return;}
        if (currentVersion == LogicalNodeUniqueValueMigrationPending)
        {
            UpgradeLogicalNodeUniqueValues(database);
            UpgradeLogicalNodeUppercaseValues(database);
            UpgradeLogicalNodeRequiredLogic(database);
            UpgradeLogicalNodeBranches(database);UpgradeLogicalNode12Logic(database);UpgradeLogicalNodeRelations(database);UpgradeLogicalNodeRelationLogic(database);UpgradeLogicalNodeCoordinates(database);UpgradeLogicalNodeInstances(database);UpgradeLogicalNodeSetRelations(database);UpgradeLogicalNodeEquivalence(database);EnsureDefaultSpeed(database);
            return;
        }
        if(currentVersion==LogicalNodeUppercaseMigrationPending)
        {UpgradeLogicalNodeUppercaseValues(database);UpgradeLogicalNodeRequiredLogic(database);UpgradeLogicalNodeBranches(database);UpgradeLogicalNode12Logic(database);UpgradeLogicalNodeRelations(database);UpgradeLogicalNodeRelationLogic(database);UpgradeLogicalNodeCoordinates(database);UpgradeLogicalNodeInstances(database);UpgradeLogicalNodeSetRelations(database);UpgradeLogicalNodeEquivalence(database);EnsureDefaultSpeed(database);return;}
        if(currentVersion==LogicalNodeRequiredLogicPending)
        {UpgradeLogicalNodeRequiredLogic(database);UpgradeLogicalNodeBranches(database);UpgradeLogicalNode12Logic(database);UpgradeLogicalNodeRelations(database);UpgradeLogicalNodeRelationLogic(database);UpgradeLogicalNodeCoordinates(database);UpgradeLogicalNodeInstances(database);UpgradeLogicalNodeSetRelations(database);UpgradeLogicalNodeEquivalence(database);EnsureDefaultSpeed(database);return;}
        if (currentVersion == 1)
        {
            foreach (var column in new[] { "Percent1", "Percent2" })
            {
                using var exists = database.CreateCommand();
                exists.CommandText = "SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=@db AND TABLE_NAME='Crosses' AND COLUMN_NAME=@column";
                exists.Parameters.AddWithValue("@db", DatabaseName);
                exists.Parameters.AddWithValue("@column", column);
                if (Convert.ToInt32(exists.ExecuteScalar()) == 0)
                {
                    using var add = database.CreateCommand();
                    add.CommandText = $"ALTER TABLE Crosses ADD COLUMN `{column}` DOUBLE NOT NULL DEFAULT 0";
                    add.ExecuteNonQuery();
                }
            }
            foreach (var (index, columns) in new[] { ("IX_Cross_Line1Segment", "Line1Id,Segment1"), ("IX_Cross_Line2Segment", "Line2Id,Segment2") })
            {
                using var exists = database.CreateCommand();
                exists.CommandText = "SELECT COUNT(*) FROM information_schema.STATISTICS WHERE TABLE_SCHEMA=@db AND TABLE_NAME='Crosses' AND INDEX_NAME=@index";
                exists.Parameters.AddWithValue("@db", DatabaseName);
                exists.Parameters.AddWithValue("@index", index);
                if (Convert.ToInt32(exists.ExecuteScalar()) == 0)
                {
                    using var add = database.CreateCommand();
                    add.CommandText = $"ALTER TABLE Crosses ADD INDEX `{index}` ({columns})";
                    add.ExecuteNonQuery();
                }
            }
            using (var key = database.CreateCommand())
            {
                key.CommandText = "ALTER TABLE Crosses DROP INDEX UQ_CrossRelation, ADD UNIQUE KEY UQ_CrossRelation (Line1Id,Line2Id,Segment1,Segment2,X2,Y2)";
                key.ExecuteNonQuery();
            }
            using var constraint = database.CreateCommand();
            constraint.CommandText = "SELECT COUNT(*) FROM information_schema.TABLE_CONSTRAINTS WHERE CONSTRAINT_SCHEMA=@db AND TABLE_NAME='Crosses' AND CONSTRAINT_NAME='CK_Cross_Position'";
            constraint.Parameters.AddWithValue("@db", DatabaseName);
            if (Convert.ToInt32(constraint.ExecuteScalar()) == 0)
            {
                using var add = database.CreateCommand();
                add.CommandText = "ALTER TABLE Crosses ADD CONSTRAINT CK_Cross_Position CHECK (Percent1 BETWEEN 0 AND 1 AND Percent2 BETWEEN 0 AND 1)";
                add.ExecuteNonQuery();
            }
            using var upgrade = database.CreateCommand();
            upgrade.CommandText = "UPDATE MapMetadata SET SchemaVersion=2 WHERE Id=1 AND SchemaVersion=1";
            upgrade.ExecuteNonQuery();
            currentVersion = 2;
        }
        if (currentVersion == 2)
        {
            AddColumnIfMissing(database, "LineNodes", "IsChanged", "INT NOT NULL DEFAULT 0");
            AddColumnIfMissing(database, "Crosses", "IsChanged", "INT NOT NULL DEFAULT 0");
            AddColumnIfMissing(database, "Crosses", "X", "DECIMAL(7,1) NULL");
            AddColumnIfMissing(database, "Crosses", "Y", "DECIMAL(7,1) NULL");
            if (ColumnExists(database, "Crosses", "X2"))
            {
                using var coordinates = database.CreateCommand();
                coordinates.CommandText = "UPDATE Crosses SET X=X2/2.0, Y=Y2/2.0";
                coordinates.ExecuteNonQuery();
            }
            using (var constraint = database.CreateCommand())
            {
                constraint.CommandText = "SELECT COUNT(*) FROM information_schema.TABLE_CONSTRAINTS WHERE CONSTRAINT_SCHEMA=@db AND TABLE_NAME='Crosses' AND CONSTRAINT_NAME='CK_Cross'";
                constraint.Parameters.AddWithValue("@db", DatabaseName);
                if (Convert.ToInt32(constraint.ExecuteScalar()) > 0)
                {
                    using var drop = database.CreateCommand();
                    drop.CommandText = "ALTER TABLE Crosses DROP CHECK CK_Cross";
                    drop.ExecuteNonQuery();
                }
            }
            using (var coordinates = database.CreateCommand())
            {
                coordinates.CommandText = "ALTER TABLE Crosses MODIFY X DECIMAL(7,1) NOT NULL, MODIFY Y DECIMAL(7,1) NOT NULL, " +
                    "DROP INDEX UQ_CrossRelation, ADD UNIQUE KEY UQ_CrossRelation (Line1Id,Line2Id,Segment1,Segment2,X,Y), " +
                    "ADD CONSTRAINT CK_Cross CHECK (Id > 0 AND Line1Id < Line2Id AND X BETWEEN 0 AND 500000 AND Y BETWEEN 0 AND 500000 " +
                    "AND X*2=FLOOR(X*2) AND Y*2=FLOOR(Y*2) AND Segment1>=0 AND Segment2>=0 " +
                    "AND Line1Version>=0 AND Line2Version>=0 AND CrossState IN (-1,0,1) AND Version>=0)";
                coordinates.ExecuteNonQuery();
            }
            foreach (var column in new[] { "X2", "Y2" })
                if (ColumnExists(database, "Crosses", column))
                {
                    using var drop = database.CreateCommand();
                    drop.CommandText = $"ALTER TABLE Crosses DROP COLUMN `{column}`";
                    drop.ExecuteNonQuery();
                }
            using var upgrade = database.CreateCommand();
            upgrade.CommandText = "UPDATE MapMetadata SET SchemaVersion=3 WHERE Id=1 AND SchemaVersion=2";
            upgrade.ExecuteNonQuery();
            currentVersion = 3;
        }
        if (currentVersion == 3)
        {
            // Match the old road-pair + coordinate identity, retaining the oldest record's state.
            using (var duplicates = database.CreateCommand())
            {
                duplicates.CommandText = "DELETE later FROM Crosses later INNER JOIN Crosses earlier " +
                    "ON later.Line1Id=earlier.Line1Id AND later.Line2Id=earlier.Line2Id " +
                    "AND later.X=earlier.X AND later.Y=earlier.Y AND earlier.Id<later.Id";
                duplicates.ExecuteNonQuery();
            }
            using (var key = database.CreateCommand())
            {
                key.CommandText = "ALTER TABLE Crosses DROP INDEX UQ_CrossRelation, ADD UNIQUE KEY UQ_CrossRelation (Line1Id,Line2Id,X,Y), " +
                    "COMMENT='交点及通行关系，按线路对及一位小数坐标识别'";
                key.ExecuteNonQuery();
            }
            using (var mark = database.CreateCommand())
            {
                mark.CommandText = "UPDATE LineNodes a INNER JOIN LineNodes b ON a.LineId=b.LineId AND b.NodeOrder=a.NodeOrder+1 SET a.IsChanged=-1";
                mark.ExecuteNonQuery();
                mark.CommandText = "UPDATE Crosses SET IsChanged=-1";
                mark.ExecuteNonQuery();
                mark.CommandText = "UPDATE MapMetadata SET SchemaVersion=4 WHERE Id=1 AND SchemaVersion=3";
                mark.ExecuteNonQuery();
            }
            currentVersion = 4;
        }
        if (currentVersion == 4)
        {
            using var upgrade = database.CreateCommand();
            upgrade.CommandText = "UPDATE MapMetadata SET SchemaVersion=5 WHERE Id=1 AND SchemaVersion=4";
            upgrade.ExecuteNonQuery();
            currentVersion = 5;
        }
        if (currentVersion == 5)
        {
            AddColumnIfMissing(database, "DictLogin", "EditAllRoad", "BOOLEAN NOT NULL DEFAULT FALSE");
            AddColumnIfMissing(database, "Lines", "IsChanged", "INT NOT NULL DEFAULT 0");
            foreach (var (column, definition) in new[]
            {
                ("RoadName", "VARCHAR(32) NOT NULL DEFAULT ''"), ("RoadDetail", "VARCHAR(255) NOT NULL DEFAULT ''"),
                ("WalkInDirection", "SMALLINT NOT NULL DEFAULT 1"), ("WalkInOpposeDirection", "SMALLINT NOT NULL DEFAULT 1"),
                ("EBicycleInDirection", "SMALLINT NOT NULL DEFAULT 1"), ("EBicycleInOpposeDirection", "SMALLINT NOT NULL DEFAULT 1"),
                ("CarInDirection", "SMALLINT NOT NULL DEFAULT 1"), ("CarInOpposeDirection", "SMALLINT NOT NULL DEFAULT 1"),
                ("MaxSpeed", "INT NOT NULL DEFAULT 0")
            }) AddColumnIfMissing(database, "Lines", column, definition);
            using var upgrade = database.CreateCommand();
            upgrade.CommandText = "UPDATE DictLogin SET EditAllRoad=1 WHERE UserName='admin'";
            upgrade.ExecuteNonQuery();
            upgrade.CommandText = "UPDATE `Lines` l INNER JOIN LineNodes n ON l.Id=n.LineId SET l.IsChanged=-1 WHERE n.IsChanged<0";
            upgrade.ExecuteNonQuery();
            upgrade.CommandText = "UPDATE `Lines` l INNER JOIN Crosses c ON l.Id=c.Line1Id OR l.Id=c.Line2Id SET l.IsChanged=-1 WHERE c.IsChanged<0";
            upgrade.ExecuteNonQuery();
            upgrade.CommandText = "UPDATE MapMetadata SET SchemaVersion=6 WHERE Id=1 AND SchemaVersion=5";
            upgrade.ExecuteNonQuery();
            currentVersion = 6;
        }
        if (currentVersion == 6)
        {
            UpgradeSpeedProperties(database);
            using var upgrade = database.CreateCommand();
            upgrade.CommandText = "UPDATE MapMetadata SET SchemaVersion=7 WHERE Id=1 AND SchemaVersion=6";
            upgrade.ExecuteNonQuery();
            currentVersion = 7;
        }
        if (currentVersion == 7)
        {
            var changes = new List<string>();
            if (!ColumnExists(database, "Lines", "PassInDirection")) changes.Add("ADD COLUMN PassInDirection SMALLINT NOT NULL DEFAULT 1");
            if (!ColumnExists(database, "Lines", "PassInOpposeDirection")) changes.Add("ADD COLUMN PassInOpposeDirection SMALLINT NOT NULL DEFAULT 0");
            using var check = database.CreateCommand();
            check.CommandText = "SELECT COUNT(*) FROM information_schema.TABLE_CONSTRAINTS WHERE CONSTRAINT_SCHEMA=@db AND TABLE_NAME='Lines' AND CONSTRAINT_NAME='CK_Lines_PassDirection'";
            check.Parameters.AddWithValue("@db", DatabaseName);
            if (Convert.ToInt32(check.ExecuteScalar()) == 0) changes.Add("ADD CONSTRAINT CK_Lines_PassDirection CHECK (PassInDirection=1 AND PassInOpposeDirection IN (0,1))");
            if (changes.Count > 0)
            {
                using var alter = database.CreateCommand(); alter.CommandText = "ALTER TABLE `Lines` " + string.Join(",", changes); alter.ExecuteNonQuery();
            }
            using var upgrade = database.CreateCommand();
            upgrade.CommandText = "UPDATE MapMetadata SET SchemaVersion=8 WHERE Id=1 AND SchemaVersion=7";
            upgrade.ExecuteNonQuery();
            currentVersion = 8;
        }
        else if (currentVersion is not (8 or 9 or 10 or 11 or 12 or 13 or 14 or 15 or 16 or 17 or 18 or 19 or 20 or 21 or 22 or 23 or 24 or 25 or 26 or 27 or 28 or 29 or 30 or 31 or 32 or 33 or 34 or 35)) throw new InvalidOperationException("数据库架构版本不兼容。");
        if (currentVersion < 9) UpgradeCodeSchema(database);
        else DropMigrationTables(database, "_NumericV8");
        UpgradeCrossDirections(database);
        UpgradeLogicalNodeSchema(database);
        UpgradeLogicalNodePositions(database);
        UpgradeLogicalNodeStart(database);
        UpgradeLogicalNodeMetadata(database);
        UpgradeCrossRoadNames(database);
        UpgradePercentDecimals(database);
        UpgradeLogicalNodeLogicLanguage(database);
        UpgradeSmallLogicalNodes(database);
        UpgradeLogicalNodeUniqueValues(database);
        UpgradeLogicalNodeUppercaseValues(database);
        UpgradeLogicalNodeRequiredLogic(database);
        UpgradeLogicalNodeBranches(database);UpgradeLogicalNode12Logic(database);UpgradeLogicalNodeRelations(database);UpgradeLogicalNodeRelationLogic(database);UpgradeLogicalNodeCoordinates(database);UpgradeLogicalNodeInstances(database);UpgradeLogicalNodeSetRelations(database);UpgradeLogicalNodeEquivalence(database);EnsureDefaultSpeed(database);
        }
        finally
        {
            using var release = database.CreateCommand();
            release.CommandText = "SELECT RELEASE_LOCK(@name)";
            release.Parameters.AddWithValue("@name", lockName);
            release.ExecuteScalar();
        }
    }

    private bool ColumnExists(MySqlConnection database, string table, string column)
    {
        using var command = database.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=@db AND TABLE_NAME=@table AND COLUMN_NAME=@column";
        command.Parameters.AddWithValue("@db", DatabaseName);
        command.Parameters.AddWithValue("@table", table);
        command.Parameters.AddWithValue("@column", column);
        return Convert.ToInt32(command.ExecuteScalar()) > 0;
    }

    // Called under the schema advisory lock after all upgrades, including resumed ones.
    // Changing only the DEFAULT preserves every stored speed and map version.
    private void EnsureDefaultSpeed(MySqlConnection database)
    {
        InitializeUndoHistory(database);
        UpgradeLogicalNodeOperandKind(database);
        UpgradeLogicalNodeInstanceAttributes(database);
        using var check=database.CreateCommand();
        check.CommandText="SELECT COLUMN_DEFAULT FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=@db AND TABLE_NAME='Lines' AND COLUMN_NAME='Speed'";
        check.Parameters.AddWithValue("@db",DatabaseName);
        if(Convert.ToString(check.ExecuteScalar(),System.Globalization.CultureInfo.InvariantCulture)==MapLimits.SpeedDefault.ToString(System.Globalization.CultureInfo.InvariantCulture))return;
        using var alter=database.CreateCommand();
        alter.CommandText=$"ALTER TABLE `Lines` ALTER COLUMN Speed SET DEFAULT {MapLimits.SpeedDefault}";
        alter.ExecuteNonQuery();
    }

    private void UpgradeSpeedProperties(MySqlConnection database)
    {
        var hasSpeed = ColumnExists(database, "Lines", "Speed");
        var hasOldSpeed = ColumnExists(database, "Lines", "MaxSpeed");
        var source = hasSpeed ? "Speed" : hasOldSpeed ? "MaxSpeed" : null;
        if (source is not null)
        {
            using var transaction = database.BeginTransaction(IsolationLevel.ReadCommitted);
            try
            {
                using var gate = Command(database, transaction, "SELECT Id FROM MapMetadata WHERE Id=1 FOR UPDATE");
                gate.ExecuteScalar();
                Execute(database, transaction,
                    $"UPDATE `Lines` SET `{source}`=5,Version=Version+1,UpdatedAt=UTC_TIMESTAMP(6) WHERE `{source}`<5 OR `{source}`>500");
                transaction.Commit();
            }
            catch { transaction.Rollback(); throw; }
        }

        // MySQL DDL commits implicitly. Keep rename, removals and CHECK in one atomic ALTER;
        // the schema advisory lock is held throughout, and a schema-6 retry inspects actual columns.
        var changes = new List<string>();
        if (!hasSpeed)
            changes.Add(hasOldSpeed ? "CHANGE COLUMN MaxSpeed Speed INT NOT NULL DEFAULT 5" : "ADD COLUMN Speed INT NOT NULL DEFAULT 5");
        else changes.Add("MODIFY COLUMN Speed INT NOT NULL DEFAULT 5");
        foreach (var column in new[] { "WalkInDirection", "WalkInOpposeDirection", "EBicycleInDirection", "EBicycleInOpposeDirection", "CarInDirection", "CarInOpposeDirection" })
            if (ColumnExists(database, "Lines", column)) changes.Add($"DROP COLUMN `{column}`");
        if (hasSpeed && hasOldSpeed) changes.Add("DROP COLUMN MaxSpeed");
        using (var check = database.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(*) FROM information_schema.TABLE_CONSTRAINTS WHERE CONSTRAINT_SCHEMA=@db AND TABLE_NAME='Lines' AND CONSTRAINT_NAME='CK_Lines_Speed'";
            check.Parameters.AddWithValue("@db", DatabaseName);
            if (Convert.ToInt32(check.ExecuteScalar()) == 0)
                changes.Add("ADD CONSTRAINT CK_Lines_Speed CHECK (Speed BETWEEN 5 AND 500)");
        }
        using var alter = database.CreateCommand();
        alter.CommandText = "ALTER TABLE `Lines` " + string.Join(",", changes);
        alter.ExecuteNonQuery();
    }

    private void AddColumnIfMissing(MySqlConnection database, string table, string column, string definition)
    {
        if (ColumnExists(database, table, column)) return;
        using var command = database.CreateCommand();
        command.CommandText = $"ALTER TABLE `{table}` ADD COLUMN `{column}` {definition}";
        command.ExecuteNonQuery();
    }

    public T Transaction<T>(Func<LineStoreState, T> operation)
    {
        using var connection = Open();
        using var schemaLease = AcquireSchemaLease(connection);
        using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
        try
        {
            var state = ReadState(connection, transaction);
            state.UndoHistory = ReadUndoHistory(connection, transaction);
            var previousLines = state.Lines.ToDictionary(p => p.Key, p => p.Value with { Nodes = p.Value.Nodes.ToArray() }, StringComparer.Ordinal);
            var previousCrosses = new Dictionary<CrossKey, CrossSnapshot>(state.Crosses);
            var previousPending = state.PendingSegments.ToHashSet();
            var previousStale = state.StaleCrosses.ToHashSet();
            var previousDirty = state.DirtyLines.ToHashSet(StringComparer.Ordinal);
            var previousAccounts = new Dictionary<string, LoginAccount>(state.Accounts, StringComparer.Ordinal);
            var previousPositions = new Dictionary<LogicalNodePositionKey, LogicalNodePosition>(state.LogicalNodePositions);
            var previousLogicalNodes = new Dictionary<string, LogicalNodeSnapshot>(state.LogicalNodes, StringComparer.Ordinal);
            var result = operation(state);
            if (result is IServiceResult { Success: false }) { transaction.Rollback(); return result; }
            LogicalNodeTextRules.EnsureChangesValid(previousLogicalNodes.Values,state.LogicalNodes.Values);
            LogicalNodeCoordinateRules.EnsureValid(state.LogicalNodes.Values);
            LogicalNodeValueUniqueness.EnsureValid(state.LogicalNodes.Values);
            LogicalNodeLogicRules.EnsureValid(state.LogicalNodes.Values);
            LogicalNodeOperandRules.EnsureValid(state.LogicalNodes.Values);
            LogicalNodeBranchRules.EnsureValid(state.LogicalNodes.Values);
            LogicalNodeRelationRules.EnsureValid(state.LogicalNodes.Values);
            LogicalNodeInstanceRules.EnsureValid(state.LogicalNodes.Values);
            LogicalNodeInstanceRules.EnsureChangesValid(previousLogicalNodes.Values,state.LogicalNodes.Values);
            if(state.UndoHistory.ValidateDeletion(previousLogicalNodes.Values,state.LogicalNodes.Values) is {} instanceDeletion)
                throw new InvalidLogicalNodeInstanceException(instanceDeletion);
            var translationPercents=ReadTranslationPercents(connection,transaction,state,previousLines,previousCrosses,previousPositions);
            // The whole operation remains one transaction: original Crosses first become
            // pending (-1); recalculated upserts restore 0, and unrecovered rows are deleted.
            foreach(var key in state.CrossRecalculationMarks)
                if(previousCrosses.ContainsKey(key))ExecuteCross(connection,transaction,"UPDATE Crosses SET IsChanged=-1 WHERE "+CrossWhere,key);
            foreach(var key in state.LogicalNodePositionRecalculationMarks)
                if(previousPositions.ContainsKey(key))Execute(connection,transaction,
                    "UPDATE LogicalNodePositions SET IsChanged=-1 WHERE LogicalNodeCode=@ln AND RoadCode=@road AND RoadOrder=@order",
                    ("@ln",key.LogicalNodeCode),("@road",key.RoadCode),("@order",key.RoadOrder));
            DeleteLogicalNodePositions(connection, transaction, state, previousPositions, previousLines);
            Persist(connection, transaction, state, previousLines, previousCrosses,translationPercents.Crosses);
            PersistLogicalNodes(connection, transaction, state, previousLogicalNodes);
            PersistLogicalNodePositions(connection, transaction, state, previousPositions, previousLines,translationPercents.Positions);
            foreach (var account in state.Accounts.Values)
                if (!previousAccounts.TryGetValue(account.UserName, out var old) || old != account)
                    Execute(connection, transaction,
                        "INSERT INTO DictLogin (UserName,Md5Password,LoginCode,EditAllRoad,EditAllLogicalNodes) VALUES (@name,@password,@code,@rights,@nodes) ON DUPLICATE KEY UPDATE Md5Password=@password,LoginCode=@code,EditAllRoad=@rights,EditAllLogicalNodes=@nodes",
                        ("@name", account.UserName), ("@password", account.Md5Password), ("@code", account.LoginCode), ("@rights", account.EditAllRoad), ("@nodes", account.EditAllLogicalNodes));
            foreach (var segment in previousPending.Union(state.PendingSegments))
                if (previousPending.Contains(segment) != state.PendingSegments.Contains(segment) ||
                    !previousLines.TryGetValue(segment.LineCode, out var old) || !state.Lines.TryGetValue(segment.LineCode, out var line) || !old.Nodes.SequenceEqual(line.Nodes))
                    Execute(connection, transaction, "UPDATE LineNodes SET IsChanged=@changed WHERE LineCode=@code AND NodeOrder=@segment",
                        ("@changed", state.PendingSegments.Contains(segment) ? -1 : 0), ("@code", segment.LineCode), ("@segment", segment.Segment));
            foreach (var key in previousStale.Union(state.StaleCrosses))
                if (previousStale.Contains(key) != state.StaleCrosses.Contains(key))
                    ExecuteCross(connection, transaction, "UPDATE Crosses SET IsChanged=@changed WHERE " + CrossWhere, key,
                        ("@changed", state.StaleCrosses.Contains(key) ? -1 : 0));
            foreach (var code in previousDirty.Union(state.DirtyLines))
                if (previousDirty.Contains(code) != state.DirtyLines.Contains(code))
                    Execute(connection, transaction, "UPDATE `Lines` SET IsChanged=@changed WHERE LineCode=@code",
                        ("@changed", state.DirtyLines.Contains(code) ? -1 : 0), ("@code", code));
            PersistUndoHistory(connection, transaction, state.UndoHistory);
            transaction.Commit();
            return result;
        }
        catch
        {
            try { transaction.Rollback(); }
            catch (Exception error) when (error is MySqlException or InvalidOperationException) { }
            throw;
        }
    }

    private MySqlConnection Open()
    {
        var connection = new MySqlConnection(connectionString);
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }

    private static LineStoreState ReadState(MySqlConnection connection, MySqlTransaction transaction)
    {
        var state = new LineStoreState();
        using (var gate = Command(connection, transaction, "SELECT SchemaVersion FROM MapMetadata WHERE Id=1 FOR UPDATE"))
            if (Convert.ToInt32(gate.ExecuteScalar()) != CurrentSchemaVersion) throw new InvalidOperationException("数据库架构已变化，请重新启动客户端。");
        using (var command = Command(connection, transaction, "SELECT UserName,Md5Password,LoginCode,EditAllRoad,EditAllLogicalNodes FROM DictLogin"))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) state.Accounts.Add(reader.GetString(0), new(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetBoolean(3), reader.GetBoolean(4)));
        using (var command = Command(connection, transaction, "SELECT LineCode,Owner,IsLocked,Version,RoadName,RoadDetail,Speed,IsChanged,PassInDirection,PassInOpposeDirection FROM `Lines` ORDER BY LineCode"))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                var code = reader.GetString(0);
                state.Lines.Add(code, new(code, reader.GetString(1), reader.GetBoolean(2), reader.GetInt64(3), [])
                {
                    Properties = new() { RoadName = reader.GetString(4), RoadDetail = reader.GetString(5), Speed = reader.GetInt32(6),
                        PassInDirection = reader.GetInt16(8), PassInOpposeDirection = reader.GetInt16(9) }
                });
                if (reader.GetInt32(7) < 0) state.DirtyLines.Add(code);
            }
        var nodes = new Dictionary<string, List<LineNode>>(StringComparer.Ordinal);
        using (var command = Command(connection, transaction, "SELECT LineCode,NodeOrder,X,Y,IsChanged FROM LineNodes ORDER BY LineCode,NodeOrder"))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                var code = reader.GetString(0);
                if (!nodes.TryGetValue(code, out var list)) nodes.Add(code, list = []);
                list.Add(new(code, reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3)));
                if (reader.GetInt32(4) < 0) state.PendingSegments.Add((code, reader.GetInt32(1)));
            }
        foreach (var (code, list) in nodes) state.Lines[code] = state.Lines[code] with { Nodes = list.ToArray() };
        using (var command = Command(connection, transaction, "SELECT RoadCode1,RoadCode2,X,Y,RoadOrder1,RoadOrder2,RoadVersion1,RoadVersion2,CrossState,Version,Percent1,Percent2,IsChanged FROM Crosses ORDER BY RoadCode1,RoadCode2,X,Y"))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                var key = new CrossKey(reader.GetString(0), reader.GetString(1), new(checked((int)(reader.GetDecimal(2) * 2)), checked((int)(reader.GetDecimal(3) * 2))));
                state.Crosses.Add(key, new(key, reader.GetInt32(4), reader.GetInt32(5), reader.GetInt64(6), reader.GetInt64(7), (CrossState)reader.GetInt32(8), reader.GetInt64(9), ReadPercent(reader, 10), ReadPercent(reader, 11)));
                if (reader.GetInt32(12) < 0) state.StaleCrosses.Add(key);
            }
        ReadLogicalNodes(connection, transaction, state);
        return state;
    }

    private static void Persist(MySqlConnection connection, MySqlTransaction transaction, LineStoreState state,
        Dictionary<string, LineSnapshot> beforeLines, Dictionary<CrossKey, CrossSnapshot> beforeCrosses,
        Dictionary<CrossKey,(decimal First,decimal Second)> translatedPercents)
    {
        foreach (var key in beforeCrosses.Keys)
            if (!state.Crosses.ContainsKey(key)) ExecuteCross(connection, transaction, "DELETE FROM Crosses WHERE " + CrossWhere, key);
        var changed = state.Lines.Values.Where(l => !beforeLines.TryGetValue(l.Code, out var old) || !Equal(old, l)).ToArray();
        var changedShapes = changed.Where(l => !beforeLines.TryGetValue(l.Code, out var old) || !old.Nodes.SequenceEqual(l.Nodes)).Select(l => l.Code).ToHashSet(StringComparer.Ordinal);
        foreach (var code in changedShapes) Execute(connection, transaction, "DELETE FROM LineNodes WHERE LineCode=@code", ("@code", code));
        foreach (var code in beforeLines.Keys.Where(c => !state.Lines.ContainsKey(c)))
            Execute(connection, transaction, "DELETE FROM `Lines` WHERE LineCode=@code", ("@code", code));
        foreach (var line in changed)
        {
            var p = line.Properties;
            Execute(connection, transaction,
                "INSERT INTO `Lines` (LineCode,Owner,IsLocked,Version,RoadName,RoadDetail,Speed,PassInDirection,PassInOpposeDirection,UpdatedAt) " +
                "VALUES (@code,@owner,@locked,@version,@name,@detail,@speed,@forward,@opposite,UTC_TIMESTAMP(6)) ON DUPLICATE KEY UPDATE " +
                "Owner=@owner,IsLocked=@locked,Version=@version,RoadName=@name,RoadDetail=@detail,Speed=@speed,PassInDirection=@forward,PassInOpposeDirection=@opposite,UpdatedAt=UTC_TIMESTAMP(6)",
                ("@code", line.Code), ("@owner", line.Owner), ("@locked", line.IsLocked), ("@version", line.Version), ("@name", p.RoadName), ("@detail", p.RoadDetail),
                ("@speed", p.Speed), ("@forward", p.PassInDirection), ("@opposite", p.PassInOpposeDirection));
            if (changedShapes.Contains(line.Code)) foreach (var node in line.Nodes)
                Execute(connection, transaction, "INSERT INTO LineNodes (LineCode,NodeOrder,X,Y) VALUES (@code,@order,@x,@y)",
                    ("@code", node.LineCode), ("@order", node.Order), ("@x", node.X), ("@y", node.Y));
        }
        foreach (var cross in state.Crosses.Values)
        {
            if (beforeCrosses.TryGetValue(cross.Key, out var old) && old == cross) continue;
            var exact=translatedPercents.TryGetValue(cross.Key,out var raw);
            ExecuteCross(connection, transaction,
                "INSERT INTO Crosses (RoadCode1,RoadCode2,X,Y,RoadOrder1,RoadOrder2,RoadVersion1,RoadVersion2,CrossState,Version,Percent1,Percent2,IsChanged,UpdatedAt) " +
                "VALUES (@roadCode1,@roadCode2,@x,@y,@roadOrder1,@roadOrder2,@roadVersion1,@roadVersion2,@state,@version,@percent1,@percent2,@changed,UTC_TIMESTAMP(6)) ON DUPLICATE KEY UPDATE " +
                "RoadOrder1=@roadOrder1,RoadOrder2=@roadOrder2,Percent1=IF(@preservePercent1,Percent1,@percent1),Percent2=IF(@preservePercent2,Percent2,@percent2),RoadVersion1=@roadVersion1,RoadVersion2=@roadVersion2,CrossState=@state,Version=@version,IsChanged=@changed,UpdatedAt=UTC_TIMESTAMP(6)", cross.Key,
                ("@roadOrder1", cross.RoadOrder1), ("@roadOrder2", cross.RoadOrder2), ("@roadVersion1", cross.RoadVersion1), ("@roadVersion2", cross.RoadVersion2), ("@state", (int)cross.State),
                ("@version", cross.Version), ("@percent1", exact?raw.First:PercentDecimal(cross.Percent1)), ("@percent2", exact?raw.Second:PercentDecimal(cross.Percent2)),
                ("@changed",state.StaleCrosses.Contains(cross.Key)?-1:0),
                ("@preservePercent1", old is not null && old.Percent1 == cross.Percent1), ("@preservePercent2", old is not null && old.Percent2 == cross.Percent2));
        }
    }

    private const string CrossWhere = "RoadCode1=@roadCode1 AND RoadCode2=@roadCode2 AND X=@x AND Y=@y";
    private static void ExecuteCross(MySqlConnection connection, MySqlTransaction transaction, string sql, CrossKey key, params (string Name, object? Value)[] extra) =>
        Execute(connection, transaction, sql, new (string, object?)[] { ("@roadCode1", key.RoadCode1), ("@roadCode2", key.RoadCode2), ("@x", key.Position.X2 / 2m), ("@y", key.Position.Y2 / 2m) }.Concat(extra).ToArray());

    private static bool Equal(LineSnapshot a, LineSnapshot b) => a.Code == b.Code && a.Owner == b.Owner && a.IsLocked == b.IsLocked && a.Version == b.Version && a.Properties == b.Properties && a.Nodes.SequenceEqual(b.Nodes);

    private static MySqlCommand Command(MySqlConnection connection, MySqlTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static void Execute(MySqlConnection connection, MySqlTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql);
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }
}

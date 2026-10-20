CREATE TABLE IF NOT EXISTS MapUndoHistory (
    Sequence BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    OperationId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    Owner VARCHAR(32) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    Operation VARCHAR(64) NOT NULL,
    Payload LONGTEXT NOT NULL,
    IsUndone BOOLEAN NOT NULL DEFAULT FALSE,
    UndoRequestId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NULL,
    CreatedAt DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    UNIQUE KEY UQ_Undo_Operation (OperationId),
    UNIQUE KEY UQ_Undo_Request (UndoRequestId),
    KEY IX_Undo_Owner (Owner, IsUndone, Sequence),
    CONSTRAINT CK_Undo_Payload CHECK (JSON_VALID(Payload)),
    CONSTRAINT CK_Undo_State CHECK (IsUndone IN (0,1))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='每个用户最近100次地图操作的事务撤销记录';

CREATE TABLE IF NOT EXISTS MapEditStamps (
    ObjectKey VARCHAR(96) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
    OperationId CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='编辑归属标记，后台K/J不修改；撤销时恢复前一标记';

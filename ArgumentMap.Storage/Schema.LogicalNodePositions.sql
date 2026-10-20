CREATE TABLE IF NOT EXISTS LogicalNodePositions (
    LogicalNodeCode VARCHAR(10) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    RoadCode VARCHAR(10) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    RoadOrder INT NOT NULL,
    RoadPercent DECIMAL(20,18) NOT NULL,
    X DECIMAL(7,1) NOT NULL COMMENT '中心到线段的垂足 X',
    Y DECIMAL(7,1) NOT NULL COMMENT '中心到线段的垂足 Y',
    RoadVersion BIGINT NOT NULL,
    IsChanged INT NOT NULL DEFAULT 1,
    UpdatedAt DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    PRIMARY KEY (LogicalNodeCode,RoadCode,RoadOrder),
    KEY IX_LNP_Segment (RoadCode,RoadOrder),
    CONSTRAINT FK_LNP_LN_V12 FOREIGN KEY (LogicalNodeCode) REFERENCES LogicalNodes(Code) ON DELETE CASCADE,
    CONSTRAINT FK_LNP_Segment_V12 FOREIGN KEY (RoadCode,RoadOrder) REFERENCES LineNodes(LineCode,NodeOrder) ON DELETE CASCADE,
    CONSTRAINT CK_LNP_V12 CHECK (REGEXP_LIKE(LogicalNodeCode,'^[A-Z]{10}$','c') AND REGEXP_LIKE(RoadCode,'^[A-Z]{10}$','c')
        AND RoadOrder>=0 AND RoadPercent BETWEEN 0 AND 1 AND RoadVersion>=0 AND IsChanged IN (-1,1)
        AND X BETWEEN 0 AND 500000 AND Y BETWEEN 0 AND 500000 AND X*2=FLOOR(X*2) AND Y*2=FLOOR(Y*2))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci COMMENT='LN 一对多线段位置，垂足坐标及顺序位置';

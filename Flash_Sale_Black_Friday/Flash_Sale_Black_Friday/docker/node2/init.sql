USE master;
GO

-- =====================================================================
-- SHARD 1: FlashSale_Order_Odd (ProductId LẺ)
-- =====================================================================
IF EXISTS (SELECT name FROM sys.databases WHERE name = N'FlashSale_Order_Odd')
BEGIN
    ALTER DATABASE FlashSale_Order_Odd SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE FlashSale_Order_Odd;
END
GO

CREATE DATABASE FlashSale_Order_Odd;
GO

USE FlashSale_Order_Odd;
GO

-- Bảng Orders
CREATE TABLE dbo.Orders
(
    OrderId     INT             IDENTITY(1, 2) PRIMARY KEY,  -- 1, 3, 5, 7…
    ProductId   INT             NOT NULL,
    ThreadId    NVARCHAR(50)    NOT NULL,
    Quantity    INT             NOT NULL DEFAULT 1,
    UnitPrice   DECIMAL(18,2)   NOT NULL,
    Status      NVARCHAR(20)    NOT NULL DEFAULT 'SUCCESS',
    Method      NVARCHAR(50)    NOT NULL,
    CreatedAt   DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_Orders_OddShard CHECK (ProductId % 2 = 1)
);
GO

CREATE INDEX IX_Orders_Method    ON dbo.Orders(Method);
CREATE INDEX IX_Orders_ProductId ON dbo.Orders(ProductId);
GO

-- Bảng PurchaseLog
CREATE TABLE dbo.PurchaseLog
(
    LogId       BIGINT          IDENTITY(1, 2) PRIMARY KEY,  -- 1, 3, 5, 7…
    ProductId   INT             NOT NULL,
    ThreadId    NVARCHAR(50)    NOT NULL,
    Method      NVARCHAR(50)    NOT NULL,
    Action      NVARCHAR(20)    NOT NULL,
    StockBefore INT             NULL,
    StockAfter  INT             NULL,
    Message     NVARCHAR(500)   NULL,
    Duration_Ms DECIMAL(10,2)   NULL,
    CreatedAt   DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_PurchaseLog_OddShard CHECK (ProductId % 2 = 1)
);
GO

CREATE INDEX IX_PurchaseLog_Method    ON dbo.PurchaseLog(Method);
CREATE INDEX IX_PurchaseLog_ProductId ON dbo.PurchaseLog(ProductId);
GO

-- SP Reset
CREATE OR ALTER PROCEDURE dbo.sp_ResetShardData
    @ProductId INT
AS
BEGIN
    SET NOCOUNT ON;
    IF @ProductId % 2 <> 1
        RAISERROR(N'ProductId chẵn không thuộc shard Odd.', 16, 1);
    DELETE FROM dbo.Orders WHERE ProductId = @ProductId;
    DELETE FROM dbo.PurchaseLog WHERE ProductId = @ProductId;
    PRINT N'✅ Đã reset shard Odd cho ProductId=' + CAST(@ProductId AS NVARCHAR);
END;
GO

-- View tổng hợp
CREATE OR ALTER VIEW dbo.vw_LocalSummary
AS
SELECT
    'Odd' AS ShardName, Method,
    COUNT(*) AS TotalAttempts,
    SUM(CASE WHEN Action = 'SUCCESS' THEN 1 ELSE 0 END) AS SuccessCount,
    SUM(CASE WHEN Action = 'FAILED'  THEN 1 ELSE 0 END) AS FailedCount,
    SUM(CASE WHEN Action = 'ERROR'   THEN 1 ELSE 0 END) AS ErrorCount,
    AVG(Duration_Ms) AS AvgDuration_Ms
FROM dbo.PurchaseLog
WHERE Action IN ('SUCCESS', 'FAILED', 'ERROR')
GROUP BY Method;
GO

PRINT N'✅ FlashSale_Order_Odd đã sẵn sàng (ProductId LẺ)';
GO

-- =====================================================================
-- SHARD 2: FlashSale_Order_Even (ProductId CHẴN)
-- =====================================================================
USE master;
GO

IF EXISTS (SELECT name FROM sys.databases WHERE name = N'FlashSale_Order_Even')
BEGIN
    ALTER DATABASE FlashSale_Order_Even SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE FlashSale_Order_Even;
END
GO

CREATE DATABASE FlashSale_Order_Even;
GO

USE FlashSale_Order_Even;
GO

-- Bảng Orders
CREATE TABLE dbo.Orders
(
    OrderId     INT             IDENTITY(2, 2) PRIMARY KEY,  -- 2, 4, 6, 8…
    ProductId   INT             NOT NULL,
    ThreadId    NVARCHAR(50)    NOT NULL,
    Quantity    INT             NOT NULL DEFAULT 1,
    UnitPrice   DECIMAL(18,2)   NOT NULL,
    Status      NVARCHAR(20)    NOT NULL DEFAULT 'SUCCESS',
    Method      NVARCHAR(50)    NOT NULL,
    CreatedAt   DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_Orders_EvenShard CHECK (ProductId % 2 = 0)
);
GO

CREATE INDEX IX_Orders_Method    ON dbo.Orders(Method);
CREATE INDEX IX_Orders_ProductId ON dbo.Orders(ProductId);
GO

-- Bảng PurchaseLog
CREATE TABLE dbo.PurchaseLog
(
    LogId       BIGINT          IDENTITY(2, 2) PRIMARY KEY,  -- 2, 4, 6, 8…
    ProductId   INT             NOT NULL,
    ThreadId    NVARCHAR(50)    NOT NULL,
    Method      NVARCHAR(50)    NOT NULL,
    Action      NVARCHAR(20)    NOT NULL,
    StockBefore INT             NULL,
    StockAfter  INT             NULL,
    Message     NVARCHAR(500)   NULL,
    Duration_Ms DECIMAL(10,2)   NULL,
    CreatedAt   DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_PurchaseLog_EvenShard CHECK (ProductId % 2 = 0)
);
GO

CREATE INDEX IX_PurchaseLog_Method    ON dbo.PurchaseLog(Method);
CREATE INDEX IX_PurchaseLog_ProductId ON dbo.PurchaseLog(ProductId);
GO

-- SP Reset
CREATE OR ALTER PROCEDURE dbo.sp_ResetShardData
    @ProductId INT
AS
BEGIN
    SET NOCOUNT ON;
    IF @ProductId % 2 <> 0
        RAISERROR(N'ProductId lẻ không thuộc shard Even.', 16, 1);
    DELETE FROM dbo.Orders WHERE ProductId = @ProductId;
    DELETE FROM dbo.PurchaseLog WHERE ProductId = @ProductId;
    PRINT N'✅ Đã reset shard Even cho ProductId=' + CAST(@ProductId AS NVARCHAR);
END;
GO

-- View tổng hợp
CREATE OR ALTER VIEW dbo.vw_LocalSummary
AS
SELECT
    'Even' AS ShardName, Method,
    COUNT(*) AS TotalAttempts,
    SUM(CASE WHEN Action = 'SUCCESS' THEN 1 ELSE 0 END) AS SuccessCount,
    SUM(CASE WHEN Action = 'FAILED'  THEN 1 ELSE 0 END) AS FailedCount,
    SUM(CASE WHEN Action = 'ERROR'   THEN 1 ELSE 0 END) AS ErrorCount,
    AVG(Duration_Ms) AS AvgDuration_Ms
FROM dbo.PurchaseLog
WHERE Action IN ('SUCCESS', 'FAILED', 'ERROR')
GROUP BY Method;
GO

PRINT N'✅ FlashSale_Order_Even đã sẵn sàng (ProductId CHẴN)';
GO

-- =====================================================================
-- HOÀN TẤT
-- =====================================================================
USE master;
GO

PRINT N'';
PRINT N'=====================================================================';
PRINT N'✅ NODE2: CẢ 2 SHARD đã sẵn sàng!';
PRINT N'📌 Container: sql-node2:1435';
PRINT N'';
PRINT N'📂 FlashSale_Order_Odd  → ProductId % 2 = 1 (OrderId: 1,3,5...)';
PRINT N'📂 FlashSale_Order_Even → ProductId % 2 = 0 (OrderId: 2,4,6...)';
PRINT N'';
PRINT N'🎯 Kịch bản test: ProductId=1 → tất cả vào shard Odd';
PRINT N'=====================================================================';
GO
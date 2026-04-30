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

-- =====================================================================
-- 1. BẢNG ORDERS — chỉ chứa đơn của ProductId CHẴN
-- =====================================================================
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

-- =====================================================================
-- 2. BẢNG PURCHASE LOG
-- =====================================================================
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

-- =====================================================================
-- 3. STORED PROCEDURE: Reset shard data
-- =====================================================================
CREATE OR ALTER PROCEDURE dbo.sp_ResetShardData
    @ProductId INT
AS
BEGIN
    SET NOCOUNT ON;

    IF @ProductId % 2 <> 0
    BEGIN
        THROW 50003,
              N'ProductId lẻ không thuộc shard này — hãy chạy ở FlashSale_Order_Odd.',
              1;
    END

    DELETE FROM dbo.Orders      WHERE ProductId = @ProductId;
    DELETE FROM dbo.PurchaseLog WHERE ProductId = @ProductId;

    PRINT N'✅ Đã xoá Orders & PurchaseLog của ProductId=' +
          CAST(@ProductId AS NVARCHAR) + N' tại shard EVEN.';
END;
GO

-- =====================================================================
-- 4. VIEW: Tổng hợp cục bộ
-- =====================================================================
CREATE OR ALTER VIEW dbo.vw_LocalSummary
AS
SELECT
    'Even'                                              AS ShardName,
    Method,
    COUNT(*)                                            AS TotalAttempts,
    SUM(CASE WHEN Action = 'SUCCESS' THEN 1 ELSE 0 END) AS SuccessCount,
    SUM(CASE WHEN Action = 'FAILED'  THEN 1 ELSE 0 END) AS FailedCount,
    SUM(CASE WHEN Action = 'ERROR'   THEN 1 ELSE 0 END) AS ErrorCount,
    AVG(Duration_Ms)                                    AS AvgDuration_Ms
FROM   dbo.PurchaseLog
WHERE  Action IN ('SUCCESS', 'FAILED', 'ERROR')
GROUP  BY Method;
GO

PRINT N'';
PRINT N'✅ FlashSale_Order_Even đã sẵn sàng.';
PRINT N'📌 Kịch bản test: ProductId=1 KHÔNG tới shard này (sẽ trống).';
PRINT N'📌 Shard này vẫn cần thiết để demo query reconstruction (C4).';
GO
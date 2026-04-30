/* =====================================================================
   SCRIPT 2/3 — FlashSale_Order_Odd
   ---------------------------------------------------------------------
   Vai trò: Shard lưu Orders & PurchaseLog của các ProductId LẺ.
   
   Kịch bản test với ProductId=1:
       - Tất cả 20 luồng đều sẽ INSERT vào shard này
       - OrderId sẽ là: 1, 3, 5, 7, 9... (bước 2)
   ===================================================================== */
USE master;
GO

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

-- =====================================================================
-- 1. BẢNG ORDERS — chỉ chứa đơn của ProductId LẺ
-- =====================================================================
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

    -- CHECK constraint: shard này chỉ nhận ProductId lẻ
    CONSTRAINT CK_Orders_OddShard CHECK (ProductId % 2 = 1)
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

-- =====================================================================
-- 3. STORED PROCEDURE: Reset shard data
-- =====================================================================
CREATE OR ALTER PROCEDURE dbo.sp_ResetShardData
    @ProductId INT
AS
BEGIN
    SET NOCOUNT ON;

    IF @ProductId % 2 <> 1
    BEGIN
        THROW 50002,
              N'ProductId chẵn không thuộc shard này — hãy chạy ở FlashSale_Order_Even.',
              1;
    END

    DELETE FROM dbo.Orders      WHERE ProductId = @ProductId;
    DELETE FROM dbo.PurchaseLog WHERE ProductId = @ProductId;

    PRINT N'✅ Đã xoá Orders & PurchaseLog của ProductId=' +
          CAST(@ProductId AS NVARCHAR) + N' tại shard ODD.';
END;
GO

-- =====================================================================
-- 4. VIEW: Tổng hợp cục bộ
-- =====================================================================
CREATE OR ALTER VIEW dbo.vw_LocalSummary
AS
SELECT
    'Odd'                                               AS ShardName,
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
PRINT N'✅ FlashSale_Order_Odd đã sẵn sàng.';
PRINT N'📌 Kịch bản test: ProductId=1 (lẻ) sẽ INSERT vào shard này.';
GO
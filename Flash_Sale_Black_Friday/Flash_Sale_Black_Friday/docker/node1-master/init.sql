USE master;
GO

-- Xóa DB cũ nếu có
IF EXISTS (SELECT name FROM sys.databases WHERE name = N'FlashSale_Master')
BEGIN
    ALTER DATABASE FlashSale_Master SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE FlashSale_Master;
END
GO

CREATE DATABASE FlashSale_Master;
GO

USE FlashSale_Master;
GO

-- =====================================================================
-- BẢNG PRODUCTS — Không phân mảnh (global table)
-- =====================================================================
CREATE TABLE dbo.Products
(
    ProductId       INT             NOT NULL PRIMARY KEY,
    ProductName     NVARCHAR(200)   NOT NULL,
    OriginalPrice   DECIMAL(18,2)   NOT NULL,
    SalePrice       DECIMAL(18,2)   NOT NULL,
    Stock           INT             NOT NULL DEFAULT 0,
    Version         INT             NOT NULL DEFAULT 1,
    CreatedAt       DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
    UpdatedAt       DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

-- =====================================================================
-- DỮ LIỆU MẪU — CHỈ 1 SẢN PHẨM
-- =====================================================================
INSERT INTO dbo.Products (ProductId, ProductName, OriginalPrice, SalePrice, Stock)
VALUES (1, N'Laptop Gaming XYZ', 25000000, 9999000, 1);
GO

-- =====================================================================
-- STORED PROCEDURE: Reset Stock
-- =====================================================================
CREATE OR ALTER PROCEDURE dbo.sp_ResetStock
    @ProductId  INT,
    @Stock      INT = 1
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE dbo.Products
       SET Stock     = @Stock,
           Version   = 1,
           UpdatedAt = SYSUTCDATETIME()
     WHERE ProductId = @ProductId;

    IF @@ROWCOUNT = 0
        RAISERROR(N'ProductId không tồn tại.', 16, 1);

    PRINT N'✅ Reset ProductId=' + CAST(@ProductId AS NVARCHAR) + 
          N' về Stock=' + CAST(@Stock AS NVARCHAR);
END;
GO

-- =====================================================================
-- KIỂM TRA
-- =====================================================================
SELECT ProductId, ProductName, Stock,
       CASE WHEN ProductId % 2 = 0 THEN N'Even_Shard' ELSE N'Odd_Shard' END AS TargetShard
FROM dbo.Products;
GO

PRINT N'';
PRINT N'✅ NODE1-MASTER: FlashSale_Master đã sẵn sàng!';
PRINT N'📌 Container: sql-node1-master:1436';
PRINT N'📌 ProductId=1 (lẻ) → Orders sẽ vào FlashSale_Order_Odd (node2)';
GO
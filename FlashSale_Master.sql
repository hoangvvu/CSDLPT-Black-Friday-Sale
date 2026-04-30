USE master;
GO

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
-- 1. BẢNG PRODUCTS — Kho hàng (global, không phân mảnh)
-- =====================================================================
CREATE TABLE dbo.Products
(
    ProductId       INT             NOT NULL PRIMARY KEY,
    ProductName     NVARCHAR(200)   NOT NULL,
    OriginalPrice   DECIMAL(18,2)   NOT NULL,
    SalePrice       DECIMAL(18,2)   NOT NULL,
    Stock           INT             NOT NULL DEFAULT 0,
    Version         INT             NOT NULL DEFAULT 1,     -- Cho Optimistic Lock
    CreatedAt       DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
    UpdatedAt       DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

-- =====================================================================
-- 2. DỮ LIỆU MẪU — CHỈ 1 SẢN PHẨM theo kịch bản test
-- =====================================================================
-- ProductId=1 (LẺ) sẽ được định tuyến vào FlashSale_Order_Odd
INSERT INTO dbo.Products (ProductId, ProductName, OriginalPrice, SalePrice, Stock)
VALUES (1, N'Laptop Gaming XYZ', 25000000, 9999000, 1);
GO

-- =====================================================================
-- 3. STORED PROCEDURE: Reset tồn kho trước mỗi lần test
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
        THROW 50001, N'ProductId không tồn tại trong FlashSale_Master.', 1;

    PRINT N'✅ Đã reset Stock của ProductId=' + CAST(@ProductId AS NVARCHAR) +
          N' về ' + CAST(@Stock AS NVARCHAR);
END;
GO

-- =====================================================================
-- 4. KIỂM TRA NHANH
-- =====================================================================
SELECT ProductId,
       ProductName,
       Stock,
       CASE WHEN ProductId % 2 = 0
            THEN N'FlashSale_Order_Even'
            ELSE N'FlashSale_Order_Odd'
       END AS N'Shard đích của Orders'
FROM   dbo.Products;
GO

PRINT N'';
PRINT N'✅ Database FlashSale_Master đã sẵn sàng.';
PRINT N'📌 Kịch bản test: ProductId=1, Stock=1, 20 luồng đua nhau.';
PRINT N'📌 Bước tiếp theo: chạy Script 2 (Odd) và Script 3 (Even).';
GO
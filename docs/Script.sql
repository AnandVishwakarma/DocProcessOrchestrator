USE imagema1_DocDB;
GO

-- 1. Documents Table
CREATE TABLE Documents (
    DocumentId UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    CustomerId NVARCHAR(50) NOT NULL,
    DocumentType NVARCHAR(20) NOT NULL, -- PAN, AADHAR, LOA
    FileName NVARCHAR(255) NOT NULL,
    FileContentBase64 NVARCHAR(MAX) NOT NULL,
    ExtractedText NVARCHAR(MAX) NULL,
    CurrentStatus NVARCHAR(50) NOT NULL,
    UploadedOn DATETIME2 DEFAULT SYSUTCDATETIME()
);
GO

-- 2. Audit Trail Table
CREATE TABLE DocumentWorkflowStatus (
    Id INT IDENTITY(1,1) PRIMARY KEY,
    DocumentId UNIQUEIDENTIFIER NOT NULL,
    Status NVARCHAR(50) NOT NULL,
    Message NVARCHAR(1000) NULL,
    Timestamp DATETIME2 DEFAULT SYSUTCDATETIME(),
    CONSTRAINT FK_WorkflowStatus_Documents FOREIGN KEY (DocumentId) REFERENCES Documents(DocumentId)
);
GO

-- 3. Compliance Rules Table
CREATE TABLE ComplianceRules (
    RuleId INT IDENTITY(1,1) PRIMARY KEY,
    DocumentType NVARCHAR(20) NOT NULL,
    RegexPattern NVARCHAR(255) NOT NULL,
    ExpectedLength INT NOT NULL,
    Description NVARCHAR(255) NOT NULL
);
GO

-- Seed Compliance Rules
INSERT INTO ComplianceRules (DocumentType, RegexPattern, ExpectedLength, Description)
VALUES 
('PAN', '^[A-Z]{5}[0-9]{4}[A-Z]{1}$', 10, 'Standard 10-digit Indian Permanent Account Number'),
('AADHAR', '^[0-9]{12}$', 12, '12-digit Indian Identification Number');
GO

-- 4. Stored Procedures
CREATE OR ALTER PROCEDURE sp_SaveDocument
    @DocumentId UNIQUEIDENTIFIER,
    @CustomerId NVARCHAR(50),
    @DocumentType NVARCHAR(20),
    @FileName NVARCHAR(255),
    @FileContentBase64 NVARCHAR(MAX),
    @Status NVARCHAR(50)
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO Documents (DocumentId, CustomerId, DocumentType, FileName, FileContentBase64, CurrentStatus)
    VALUES (@DocumentId, @CustomerId, @DocumentType, @FileName, @FileContentBase64, @Status);

    INSERT INTO DocumentWorkflowStatus (DocumentId, Status, Message)
    VALUES (@DocumentId, @Status, 'Initial document intake completed.');
END;
GO

CREATE OR ALTER PROCEDURE sp_UpdateWorkflowStatus
    @DocumentId UNIQUEIDENTIFIER,
    @Status NVARCHAR(50),
    @Message NVARCHAR(1000)
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE Documents
    SET CurrentStatus = @Status
    WHERE DocumentId = @DocumentId;

    INSERT INTO DocumentWorkflowStatus (DocumentId, Status, Message)
    VALUES (@DocumentId, @Status, @Message);
END;
GO

CREATE OR ALTER PROCEDURE sp_SaveOCRText
    @DocumentId UNIQUEIDENTIFIER,
    @ExtractedText NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE Documents
    SET ExtractedText = @ExtractedText
    WHERE DocumentId = @DocumentId;
END;
GO

CREATE OR ALTER PROCEDURE sp_GetComplianceRules
    @DocumentType NVARCHAR(20)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT RuleId, DocumentType, RegexPattern, ExpectedLength, Description
    FROM ComplianceRules
    WHERE DocumentType = @DocumentType;
END;
GO
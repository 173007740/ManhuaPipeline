-- Video megapixel tier for ComfyUI frame size (0.5 / 1 / 1.5 / 2 MP, default 1)
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Projects') AND name = 'VideoMegapixels')
BEGIN
    ALTER TABLE Projects ADD VideoMegapixels FLOAT NOT NULL CONSTRAINT DF_Projects_VideoMegapixels DEFAULT (1);
END
GO

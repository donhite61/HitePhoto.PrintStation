using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using HitePhoto.PrintStation.Core;
using HitePhoto.PrintStation.Core.Models;

namespace HitePhoto.PrintStation.Data;

/// <summary>
/// Local SQLite store for color correction history.
/// Corrections are machine-local (corrected files are on local disk),
/// so this stays in SQLite rather than MariaDB.
/// </summary>
public class CorrectionStore
{
    private readonly string _dbPath;

    public CorrectionStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HitePhoto.PrintStation");
        Directory.CreateDirectory(dir);
        _dbPath = Path.Combine(dir, "corrections.db");
        Initialize();
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }

    private void Initialize()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS color_corrections (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                order_id TEXT NOT NULL,
                image_path TEXT NOT NULL,
                corrected_path TEXT,
                exposure INTEGER DEFAULT 0,
                brightness INTEGER DEFAULT 0,
                contrast INTEGER DEFAULT 0,
                shadows INTEGER DEFAULT 0,
                highlights INTEGER DEFAULT 0,
                saturation INTEGER DEFAULT 0,
                color_temp INTEGER DEFAULT 0,
                red INTEGER DEFAULT 0,
                green INTEGER DEFAULT 0,
                blue INTEGER DEFAULT 0,
                sigmoidal_contrast INTEGER DEFAULT 0,
                clahe INTEGER DEFAULT 0,
                contrast_stretch INTEGER DEFAULT 0,
                levels INTEGER DEFAULT 0,
                auto_level INTEGER DEFAULT 0,
                auto_gamma INTEGER DEFAULT 0,
                white_balance INTEGER DEFAULT 0,
                normalize INTEGER DEFAULT 0,
                grayscale INTEGER DEFAULT 0,
                sepia INTEGER DEFAULT 0,
                created_at TEXT,
                UNIQUE(order_id, image_path)
            )
            """;
        cmd.ExecuteNonQuery();
    }

    public void SaveCorrection(string orderId, ImageCorrectionState s)
    {
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO color_corrections
                    (order_id, image_path, corrected_path, exposure, brightness, contrast, shadows,
                     highlights, saturation, color_temp, red, green, blue,
                     sigmoidal_contrast, clahe, contrast_stretch, levels,
                     auto_level, auto_gamma, white_balance, normalize, grayscale, sepia, created_at)
                VALUES
                    (@oid, @img, @corr, @ex, @br, @co, @sh, @hi, @sa, @ct, @r, @g, @b,
                     @sc, @cl, @cs, @lv, @al, @ag, @wb, @nm, @gs, @sp, @now)
                ON CONFLICT(order_id, image_path) DO UPDATE SET
                    corrected_path=@corr, exposure=@ex, brightness=@br, contrast=@co,
                    shadows=@sh, highlights=@hi, saturation=@sa, color_temp=@ct,
                    red=@r, green=@g, blue=@b, sigmoidal_contrast=@sc, clahe=@cl,
                    contrast_stretch=@cs, levels=@lv, auto_level=@al, auto_gamma=@ag,
                    white_balance=@wb, normalize=@nm, grayscale=@gs, sepia=@sp, created_at=@now
                """;

            cmd.Parameters.AddWithValue("@oid", orderId);
            cmd.Parameters.AddWithValue("@img", s.ImagePath);
            cmd.Parameters.AddWithValue("@corr", s.CorrectedPath ?? "");
            cmd.Parameters.AddWithValue("@ex", s.Exposure);
            cmd.Parameters.AddWithValue("@br", s.Brightness);
            cmd.Parameters.AddWithValue("@co", s.Contrast);
            cmd.Parameters.AddWithValue("@sh", s.Shadows);
            cmd.Parameters.AddWithValue("@hi", s.Highlights);
            cmd.Parameters.AddWithValue("@sa", s.Saturation);
            cmd.Parameters.AddWithValue("@ct", s.ColorTemp);
            cmd.Parameters.AddWithValue("@r", s.Red);
            cmd.Parameters.AddWithValue("@g", s.Green);
            cmd.Parameters.AddWithValue("@b", s.Blue);
            cmd.Parameters.AddWithValue("@sc", s.SigmoidalContrast);
            cmd.Parameters.AddWithValue("@cl", s.Clahe);
            cmd.Parameters.AddWithValue("@cs", s.ContrastStretch);
            cmd.Parameters.AddWithValue("@lv", s.Levels);
            cmd.Parameters.AddWithValue("@al", s.AutoLevel ? 1 : 0);
            cmd.Parameters.AddWithValue("@ag", s.AutoGamma ? 1 : 0);
            cmd.Parameters.AddWithValue("@wb", s.WhiteBalance ? 1 : 0);
            cmd.Parameters.AddWithValue("@nm", s.Normalize ? 1 : 0);
            cmd.Parameters.AddWithValue("@gs", s.Grayscale ? 1 : 0);
            cmd.Parameters.AddWithValue("@sp", s.Sepia ? 1 : 0);
            cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            AlertCollector.Error(AlertCategory.Database,
                "Failed to save color correction",
                orderId: orderId,
                detail: $"Attempted: save correction for '{s.ImagePath}'. Found: exception.",
                ex: ex);
        }
    }

    public List<ColorCorrectionRecord> GetCorrections(string orderId)
    {
        var results = new List<ColorCorrectionRecord>();
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT image_path, corrected_path, exposure, brightness, contrast, shadows,
                       highlights, saturation, color_temp, red, green, blue,
                       sigmoidal_contrast, clahe, contrast_stretch, levels,
                       auto_level, auto_gamma, white_balance, normalize, grayscale, sepia, created_at
                FROM color_corrections WHERE order_id = @oid ORDER BY id
                """;
            cmd.Parameters.AddWithValue("@oid", orderId);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new ColorCorrectionRecord
                {
                    ImagePath = reader.GetString(0),
                    CorrectedPath = reader.GetString(1),
                    Exposure = reader.GetInt32(2),
                    Brightness = reader.GetInt32(3),
                    Contrast = reader.GetInt32(4),
                    Shadows = reader.GetInt32(5),
                    Highlights = reader.GetInt32(6),
                    Saturation = reader.GetInt32(7),
                    ColorTemp = reader.GetInt32(8),
                    Red = reader.GetInt32(9),
                    Green = reader.GetInt32(10),
                    Blue = reader.GetInt32(11),
                    SigmoidalContrast = reader.GetInt32(12),
                    Clahe = reader.GetInt32(13),
                    ContrastStretch = reader.GetInt32(14),
                    Levels = reader.GetInt32(15),
                    AutoLevel = reader.GetInt32(16) != 0,
                    AutoGamma = reader.GetInt32(17) != 0,
                    WhiteBalance = reader.GetInt32(18) != 0,
                    Normalize = reader.GetInt32(19) != 0,
                    Grayscale = reader.GetInt32(20) != 0,
                    Sepia = reader.GetInt32(21) != 0,
                    CreatedAt = reader.GetString(22)
                });
            }
        }
        catch (Exception ex)
        {
            AlertCollector.Error(AlertCategory.Database,
                "Failed to load color corrections",
                orderId: orderId,
                detail: $"Attempted: load corrections for order '{orderId}'. Found: exception.",
                ex: ex);
        }
        return results;
    }
}

public class ColorCorrectionRecord
{
    public string ImagePath { get; set; } = "";
    public string CorrectedPath { get; set; } = "";
    public int Exposure { get; set; }
    public int Brightness { get; set; }
    public int Contrast { get; set; }
    public int Shadows { get; set; }
    public int Highlights { get; set; }
    public int Saturation { get; set; }
    public int ColorTemp { get; set; }
    public int Red { get; set; }
    public int Green { get; set; }
    public int Blue { get; set; }
    public int SigmoidalContrast { get; set; }
    public int Clahe { get; set; }
    public int ContrastStretch { get; set; }
    public int Levels { get; set; }
    public bool AutoLevel { get; set; }
    public bool AutoGamma { get; set; }
    public bool WhiteBalance { get; set; }
    public bool Normalize { get; set; }
    public bool Grayscale { get; set; }
    public bool Sepia { get; set; }
    public string CreatedAt { get; set; } = "";
}

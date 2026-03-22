using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using MagnetometerSystem.Core.Calibration;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Services;

namespace MagnetometerSystem.App.ViewModels;

/// <summary>
/// 正交度校正向导 ViewModel（4 步向导）
/// Step 1: 选择传感器  Step 2: 采集/导入数据  Step 3: 计算与结果  Step 4: 保存配置
/// </summary>
public partial class OrthogonalityCalibrationViewModel : ObservableObject
{
    private readonly IOrthogonalityService _orthogonalityService;
    private readonly DataBus _dataBus;
    private readonly List<double[]> _collectedData = new();
    private readonly List<double[]> _collectedDataSecondGroup = new();

    public OrthogonalityCalibrationViewModel(
        IOrthogonalityService orthogonalityService,
        DataBus dataBus)
    {
        _orthogonalityService = orthogonalityService;
        _dataBus = dataBus;

        ProfileName = $"正交度校正_{DateTime.Now:yyyyMMdd_HHmmss}";
        UpdateStepNavigation();
    }

    // ========== 步骤控制 (共 4 步) ==========

    [ObservableProperty]
    private int _currentStep = 1;

    [ObservableProperty]
    private bool _canGoNext;

    [ObservableProperty]
    private bool _canGoBack;

    [RelayCommand]
    private void NextStep()
    {
        if (CurrentStep >= 4) return;

        // 离开 Step 2 时自动停止采集
        if (CurrentStep == 2 && IsCollecting)
        {
            StopCollecting();
        }

        // Step 2 → Step 3：执行校验（仅展示警告，不阻止）
        if (CurrentStep == 2)
        {
            var validation = CalibrationDataValidator.Validate(_collectedData);
            DataValidation = validation;
            HasValidationWarnings = validation.Warnings.Count > 0;

            if (validation.Warnings.Count > 0)
            {
                ValidationStatusText = string.Join("\n", validation.Warnings);
            }
        }

        CurrentStep++;
    }

    [RelayCommand]
    private void PreviousStep()
    {
        if (CurrentStep <= 1) return;
        CurrentStep--;
    }

    partial void OnCurrentStepChanged(int value)
    {
        UpdateStepNavigation();
    }

    private void UpdateStepNavigation()
    {
        CanGoBack = CurrentStep > 1;
        CanGoNext = CurrentStep switch
        {
            1 => SelectedSensorType == SensorType.TriaxialFluxgate
                 || SelectedSensorType == SensorType.DualTriaxialFluxgate,
            2 => CollectedSampleCount >= 3 && !IsCollecting,
            3 => SelectedSensorType == SensorType.DualTriaxialFluxgate
                ? (CalculationResult?.Success == true && SecondCalculationResult?.Success == true)
                : CalculationResult?.Success == true,
            4 => false, // 最后一步无下一步
            _ => false
        };
    }

    // ========== Step 1 - 选择传感器 ==========

    [ObservableProperty]
    private SensorType _selectedSensorType;

    [ObservableProperty]
    private string _sensorSerial = string.Empty;

    [ObservableProperty]
    private double? _referenceFieldStrength;

    public SensorType[] AvailableSensorTypes { get; } =
    [
        SensorType.TriaxialFluxgate,
        SensorType.DualTriaxialFluxgate
    ];

    partial void OnSelectedSensorTypeChanged(SensorType value)
    {
        UpdateStepNavigation();
    }

    // ========== Step 2 - 采集/导入数据 ==========

    [ObservableProperty]
    private bool _isCollecting;

    [ObservableProperty]
    private int _collectedSampleCount;

    [ObservableProperty]
    private double _sphericityCoverage;

    [ObservableProperty]
    private string _collectionStatus = "等待开始采集或导入数据";

    [ObservableProperty]
    private CalibrationDataValidation? _dataValidation;

    [ObservableProperty]
    private string _validationStatusText = string.Empty;

    [ObservableProperty]
    private bool _hasValidationWarnings;

    public ObservableCollection<double[]> CollectedData { get; } = new();

    [RelayCommand]
    private void StartCollecting()
    {
        _collectedData.Clear();
        _collectedDataSecondGroup.Clear();
        CollectedData.Clear();
        CollectedSampleCount = 0;
        SphericityCoverage = 0;
        CollectionStatus = "采集中...";
        IsCollecting = true;

        DataValidation = null;
        ValidationStatusText = string.Empty;
        HasValidationWarnings = false;

        _dataBus.ReadingReceived += OnCalibrationDataReceived;
        UpdateStepNavigation();
    }

    private void OnCalibrationDataReceived(MagnetometerReading reading)
    {
        double[] sample1;

        if (SelectedSensorType == SensorType.DualTriaxialFluxgate)
        {
            if (reading.ChannelValues.Length < 6) return;
            sample1 = new double[] { reading.ChannelValues[0], reading.ChannelValues[1], reading.ChannelValues[2] };
            var sample2 = new double[] { reading.ChannelValues[3], reading.ChannelValues[4], reading.ChannelValues[5] };
            _collectedData.Add(sample1);
            _collectedDataSecondGroup.Add(sample2);
        }
        else
        {
            if (reading.ChannelValues.Length < 3) return;
            sample1 = new double[] { reading.ChannelValues[0], reading.ChannelValues[1], reading.ChannelValues[2] };
            _collectedData.Add(sample1);
        }

        System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
        {
            CollectedData.Add(sample1);
            CollectedSampleCount = _collectedData.Count;

            if (_collectedData.Count % 50 == 0)
            {
                UpdateCoverageEstimate();
                RunDataValidation();
            }

            UpdateStepNavigation();
        });
    }

    private void UpdateCoverageEstimate()
    {
        const int nLon = 12;
        const int nLat = 6;
        var covered = new bool[nLon, nLat];

        double cx = 0, cy = 0, cz = 0;
        int n = _collectedData.Count;
        for (int i = 0; i < n; i++)
        {
            cx += _collectedData[i][0];
            cy += _collectedData[i][1];
            cz += _collectedData[i][2];
        }
        cx /= n; cy /= n; cz /= n;

        for (int i = 0; i < n; i++)
        {
            double dx = _collectedData[i][0] - cx;
            double dy = _collectedData[i][1] - cy;
            double dz = _collectedData[i][2] - cz;
            double r = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (r < 1e-10) continue;

            double lat = Math.Asin(Math.Clamp(dz / r, -1.0, 1.0));
            double lon = Math.Atan2(dy, dx);

            int lonIdx = (int)((lon + Math.PI) / (2 * Math.PI) * nLon);
            if (lonIdx >= nLon) lonIdx = nLon - 1;
            int latIdx = (int)((lat + Math.PI / 2) / Math.PI * nLat);
            if (latIdx >= nLat) latIdx = nLat - 1;

            covered[lonIdx, latIdx] = true;
        }

        int total = nLon * nLat;
        int count = 0;
        foreach (bool c in covered)
            if (c) count++;

        SphericityCoverage = (double)count / total * 100.0;
    }

    [RelayCommand]
    private void StopCollecting()
    {
        _dataBus.ReadingReceived -= OnCalibrationDataReceived;
        IsCollecting = false;
        CollectionStatus = $"采集完成，共 {CollectedSampleCount} 个样本";

        RunDataValidation();
        UpdateStepNavigation();
    }

    [RelayCommand]
    private void ImportFromFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入三轴校正数据",
            Filter = "CSV 文件 (*.csv)|*.csv|文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            DefaultExt = ".csv"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            var lines = File.ReadAllLines(dialog.FileName);
            var importedData = new List<double[]>();
            var importedDataSecond = new List<double[]>();
            int skippedLines = 0;
            int requiredCols = SelectedSensorType == SensorType.DualTriaxialFluxgate ? 6 : 3;

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                var parts = line.Split(new[] { ',', '\t', ';' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < requiredCols) { skippedLines++; continue; }

                if (double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double bx) &&
                    double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double by) &&
                    double.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double bz))
                {
                    importedData.Add(new[] { bx, by, bz });

                    if (SelectedSensorType == SensorType.DualTriaxialFluxgate &&
                        parts.Length >= 6 &&
                        double.TryParse(parts[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double bx2) &&
                        double.TryParse(parts[4].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double by2) &&
                        double.TryParse(parts[5].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double bz2))
                    {
                        importedDataSecond.Add(new[] { bx2, by2, bz2 });
                    }
                }
                else
                {
                    skippedLines++;
                }
            }

            if (importedData.Count == 0)
            {
                CollectionStatus = "导入失败：文件中未找到有效的三轴数据";
                return;
            }

            _collectedData.Clear();
            _collectedDataSecondGroup.Clear();
            CollectedData.Clear();
            _collectedData.AddRange(importedData);
            _collectedDataSecondGroup.AddRange(importedDataSecond);
            foreach (var d in importedData)
                CollectedData.Add(d);

            CollectedSampleCount = _collectedData.Count;
            IsCollecting = false;

            string skipInfo = skippedLines > 0 ? $"（跳过 {skippedLines} 行）" : "";
            CollectionStatus = $"已从文件导入 {importedData.Count} 个样本{skipInfo}";

            UpdateCoverageEstimate();
            RunDataValidation();
            UpdateStepNavigation();
        }
        catch (Exception ex)
        {
            CollectionStatus = $"导入失败：{ex.Message}";
        }
    }

    private void RunDataValidation()
    {
        if (_collectedData.Count < 3) return;

        var validation = CalibrationDataValidator.Validate(_collectedData);
        DataValidation = validation;
        HasValidationWarnings = validation.Warnings.Count > 0;

        if (validation.Warnings.Count > 0)
        {
            ValidationStatusText = string.Join("\n", validation.Warnings);
        }
        else
        {
            ValidationStatusText = $"数据质量良好（总场 {validation.MeanTotalField:F0} nT，覆盖度 {validation.SphericityCoverage:P0}）";
        }
    }

    partial void OnCollectedSampleCountChanged(int value)
    {
        UpdateStepNavigation();
    }

    partial void OnIsCollectingChanged(bool value)
    {
        UpdateStepNavigation();
    }

    // ========== Step 3 - 计算与结果 ==========

    [ObservableProperty]
    private bool _isCalculating;

    [ObservableProperty]
    private string _calculationStatus = "点击下方按钮开始计算";

    [ObservableProperty]
    private OrthogonalityResult? _calculationResult;

    [RelayCommand]
    private async Task RunCalculation()
    {
        IsCalculating = true;
        CalculationStatus = "计算中...";

        try
        {
            var rawData = ConvertToMatrix(_collectedData);

            var result = await Task.Run(() =>
                _orthogonalityService.Calculate(rawData, ReferenceFieldStrength));

            if (result.Success)
            {
                CalculationResult = result;
                CalculationStatus = "计算完成";

                // Populate visualization data
                VisualizationRawData = rawData;
                var corrected = new double[rawData.GetLength(0), 3];
                for (int i = 0; i < rawData.GetLength(0); i++)
                {
                    var c = result.Parameters.Apply(rawData[i, 0], rawData[i, 1], rawData[i, 2]);
                    corrected[i, 0] = c[0]; corrected[i, 1] = c[1]; corrected[i, 2] = c[2];
                }
                VisualizationCorrectedData = corrected;
                OnPropertyChanged(nameof(VisualizationRawData));
                OnPropertyChanged(nameof(VisualizationCorrectedData));
            }
            else
            {
                CalculationStatus = $"计算失败: {result.ErrorMessage}";
            }

            // Run second group calculation for DualTriaxial
            if (SelectedSensorType == SensorType.DualTriaxialFluxgate && _collectedDataSecondGroup.Count >= 3)
            {
                var rawData2 = ConvertToMatrix(_collectedDataSecondGroup);
                var result2 = await Task.Run(() =>
                    _orthogonalityService.Calculate(rawData2, ReferenceFieldStrength));

                SecondCalculationResult = result2.Success ? result2 : null;
            }
        }
        catch (Exception ex)
        {
            CalculationStatus = $"计算异常: {ex.Message}";
        }
        finally
        {
            IsCalculating = false;
            UpdateStepNavigation();
        }
    }

    private static double[,] ConvertToMatrix(List<double[]> data)
    {
        int n = data.Count;
        var matrix = new double[n, 3];
        for (int i = 0; i < n; i++)
        {
            matrix[i, 0] = data[i][0];
            matrix[i, 1] = data[i][1];
            matrix[i, 2] = data[i][2];
        }
        return matrix;
    }

    partial void OnCalculationResultChanged(OrthogonalityResult? value)
    {
        OnPropertyChanged(nameof(QualityRating));
        OnPropertyChanged(nameof(MatrixM00));
        OnPropertyChanged(nameof(MatrixM01));
        OnPropertyChanged(nameof(MatrixM02));
        OnPropertyChanged(nameof(MatrixM10));
        OnPropertyChanged(nameof(MatrixM11));
        OnPropertyChanged(nameof(MatrixM12));
        OnPropertyChanged(nameof(MatrixM20));
        OnPropertyChanged(nameof(MatrixM21));
        OnPropertyChanged(nameof(MatrixM22));
        OnPropertyChanged(nameof(OffsetX));
        OnPropertyChanged(nameof(OffsetY));
        OnPropertyChanged(nameof(OffsetZ));
        UpdateStepNavigation();
    }

    /// <summary>质量评级</summary>
    public string QualityRating => CalculationResult?.Quality switch
    {
        { ResidualStd: < 10 } => "优秀",
        { ResidualStd: < 50 } => "良好",
        { ResidualStd: < 200 } => "一般",
        _ => "较差"
    };

    public string MatrixM00 => FormatMatrixValue(0);
    public string MatrixM01 => FormatMatrixValue(1);
    public string MatrixM02 => FormatMatrixValue(2);
    public string MatrixM10 => FormatMatrixValue(3);
    public string MatrixM11 => FormatMatrixValue(4);
    public string MatrixM12 => FormatMatrixValue(5);
    public string MatrixM20 => FormatMatrixValue(6);
    public string MatrixM21 => FormatMatrixValue(7);
    public string MatrixM22 => FormatMatrixValue(8);

    private string FormatMatrixValue(int index)
    {
        var m = CalculationResult?.Parameters?.CompensationMatrix;
        if (m == null || m.Length <= index) return "—";
        return m[index].ToString("F6");
    }

    public string OffsetX => CalculationResult?.Parameters?.Offset is { Length: >= 1 } o ? o[0].ToString("F4") : "—";
    public string OffsetY => CalculationResult?.Parameters?.Offset is { Length: >= 2 } o ? o[1].ToString("F4") : "—";
    public string OffsetZ => CalculationResult?.Parameters?.Offset is { Length: >= 3 } o ? o[2].ToString("F4") : "—";

    // ========== Visualization Data ==========

    public double[,]? VisualizationRawData { get; private set; }
    public double[,]? VisualizationCorrectedData { get; private set; }

    // ========== Second Group (DualTriaxial) ==========

    [ObservableProperty]
    private OrthogonalityResult? _secondCalculationResult;

    partial void OnSecondCalculationResultChanged(OrthogonalityResult? value)
    {
        OnPropertyChanged(nameof(SecondQualityRating));
        OnPropertyChanged(nameof(SecondMatrixM00));
        OnPropertyChanged(nameof(SecondMatrixM01));
        OnPropertyChanged(nameof(SecondMatrixM02));
        OnPropertyChanged(nameof(SecondMatrixM10));
        OnPropertyChanged(nameof(SecondMatrixM11));
        OnPropertyChanged(nameof(SecondMatrixM12));
        OnPropertyChanged(nameof(SecondMatrixM20));
        OnPropertyChanged(nameof(SecondMatrixM21));
        OnPropertyChanged(nameof(SecondMatrixM22));
        OnPropertyChanged(nameof(SecondOffsetX));
        OnPropertyChanged(nameof(SecondOffsetY));
        OnPropertyChanged(nameof(SecondOffsetZ));
        UpdateStepNavigation();
    }

    /// <summary>第二组质量评级</summary>
    public string SecondQualityRating => SecondCalculationResult?.Quality switch
    {
        { ResidualStd: < 10 } => "优秀",
        { ResidualStd: < 50 } => "良好",
        { ResidualStd: < 200 } => "一般",
        _ => "较差"
    };

    public string SecondMatrixM00 => FormatSecondMatrixValue(0);
    public string SecondMatrixM01 => FormatSecondMatrixValue(1);
    public string SecondMatrixM02 => FormatSecondMatrixValue(2);
    public string SecondMatrixM10 => FormatSecondMatrixValue(3);
    public string SecondMatrixM11 => FormatSecondMatrixValue(4);
    public string SecondMatrixM12 => FormatSecondMatrixValue(5);
    public string SecondMatrixM20 => FormatSecondMatrixValue(6);
    public string SecondMatrixM21 => FormatSecondMatrixValue(7);
    public string SecondMatrixM22 => FormatSecondMatrixValue(8);

    private string FormatSecondMatrixValue(int index)
    {
        var m = SecondCalculationResult?.Parameters?.CompensationMatrix;
        if (m == null || m.Length <= index) return "—";
        return m[index].ToString("F6");
    }

    public string SecondOffsetX => SecondCalculationResult?.Parameters?.Offset is { Length: >= 1 } o ? o[0].ToString("F4") : "—";
    public string SecondOffsetY => SecondCalculationResult?.Parameters?.Offset is { Length: >= 2 } o ? o[1].ToString("F4") : "—";
    public string SecondOffsetZ => SecondCalculationResult?.Parameters?.Offset is { Length: >= 3 } o ? o[2].ToString("F4") : "—";

    // ========== Step 4 - 保存配置 ==========

    [ObservableProperty]
    private string _profileName = string.Empty;

    [ObservableProperty]
    private string _profileNotes = string.Empty;

    [ObservableProperty]
    private bool _setAsDefault = true;

    [ObservableProperty]
    private string _saveStatus = string.Empty;

    [RelayCommand]
    private void SaveProfile()
    {
        if (CalculationResult?.Parameters == null)
        {
            SaveStatus = "无计算结果可保存";
            return;
        }

        if (string.IsNullOrWhiteSpace(ProfileName))
        {
            SaveStatus = "请输入配置名称";
            return;
        }

        try
        {
            var parameters = CalculationResult.Parameters;
            parameters.Name = ProfileName;
            parameters.SensorSerial = SensorSerial;
            parameters.Notes = ProfileNotes;
            parameters.ResidualMean = CalculationResult.Quality?.ResidualMean;
            parameters.ResidualStd = CalculationResult.Quality?.ResidualStd;
            parameters.SampleCount = CalculationResult.Quality?.SampleCount;

            SavedProfile = parameters;

            // Save second profile for DualTriaxial
            if (SelectedSensorType == SensorType.DualTriaxialFluxgate && SecondCalculationResult?.Parameters != null)
            {
                var secondParameters = SecondCalculationResult.Parameters;
                secondParameters.Name = $"{ProfileName}_第二组";
                secondParameters.SensorSerial = SensorSerial;
                secondParameters.Notes = ProfileNotes;
                secondParameters.ResidualMean = SecondCalculationResult.Quality?.ResidualMean;
                secondParameters.ResidualStd = SecondCalculationResult.Quality?.ResidualStd;
                secondParameters.SampleCount = SecondCalculationResult.Quality?.SampleCount;

                SavedSecondProfile = secondParameters;
            }

            SaveStatus = "已保存并应用";
        }
        catch (Exception ex)
        {
            SaveStatus = $"保存失败: {ex.Message}";
        }
    }

    [ObservableProperty]
    private OrthogonalityParams? _savedProfile;

    [ObservableProperty]
    private OrthogonalityParams? _savedSecondProfile;

    public void Cleanup()
    {
        if (IsCollecting)
        {
            _dataBus.ReadingReceived -= OnCalibrationDataReceived;
            IsCollecting = false;
        }
        _collectedData.Clear();
        _collectedDataSecondGroup.Clear();
        CollectedData.Clear();
    }
}

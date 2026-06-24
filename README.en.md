# MagnetometerSystem

Magnetometer Data Acquisition and Analysis System

## Project Overview

MagnetometerSystem is a professional WPF desktop application for magnetometer data acquisition and analysis, supporting real-time data collection, display, orthogonality calibration, and historical data playback for multiple types of magnetic sensors (tri-axial fluxgate, single-axial fluxgate, dual tri-axial fluxgate, proton magnetometer).

## Main Features

### 1. Real-Time Data Acquisition
- Supports serial port and TCP network connections
- Multiple protocol parsing (ASCII CSV, binary frames, configurable protocols)
- Protocol configuration save and import
- Real-time raw data display

### 2. Real-Time Chart Display
- Toggle between single-chart and multi-chart modes
- Supports display of up to 8 channels
- Calculated channels (total field strength, gradient computation)
- Data filtering (moving average, median filter)
- Statistical information display
- Interval selection and statistics

### 3. Orthogonality Calibration
- Two calibration modes: continuous acquisition and 48-point manual acquisition
- Data validation and quality assessment
- Ellipsoid fitting method for orthogonality calculation
- Calibration parameter storage and management
- Real-time data visualization

### 4. Historical Data Playback
- Session management and search
- Batch orthogonality correction
- Multiple playback speed controls
- Data export (CSV format)

### 5. Device Commands
- Command directory management
- ASCII and binary frame construction
- Parameterized command sending
- Communication logging

### 6. Settings
- Default connection parameter configuration
- Data storage path configuration
- Chart refresh rate settings
- Theme selection

## Technical Architecture

### Project Structure
```
src/
├── MagnetometerSystem.App/           # WPF Application
│   ├── ViewModels/                   # View Models
│   ├── Views/                        # Views
│   ├── Converters/                   # Value Converters
│   ├── Behaviors/                    # Interaction Behaviors
│   └── Helpers/                      # Helper Classes
├── MagnetometerSystem.Core/         # Core Library
│   ├── Calibration/                 # Calibration Algorithms
│   ├── Communication/               # Communication Module
│   ├── Helpers/                     # Utility Tools
│   ├── Models/                      # Data Models
│   ├── Processing/                  # Data Processing
│   ├── Protocol/                    # Protocol Parsing
│   ├── Sensors/                     # Sensor Adapters
│   └── Services/                    # Core Services
└── MagnetometerSystem.Infrastructure/# Infrastructure
    ├── Configuration/               # Configuration Services
    ├── Database/                    # Database
    ├── Export/                      # Data Export
    └── Services/                    # Infrastructure Services
```

### Technology Stack
- **Framework**: .NET 8 + WPF
- **MVVM**: CommunityToolkit.Mvvm
- **Charts**: ScottPlot
- **Database**: SQLite
- **Dependency Injection**: Microsoft.Extensions.DependencyInjection

## System Requirements

- .NET 8.0 SDK
- Windows 10/11

## Build and Run

### Build
```bash
dotnet build MagnetometerSystem.sln
```

### Run
```bash
dotnet run --project src/MagnetometerSystem.App/MagnetometerSystem.App.csproj
```

## Usage Instructions

### Connecting Sensors
1. Select connection type (Serial Port/TCP)
2. Configure connection parameters (baud rate, IP address, etc.)
3. Select sensor type and sampling rate
4. Configure protocol (optional)
5. Click the Connect button

### Orthogonality Calibration Process
1. Navigate to the "Orthogonality Calibration" interface
2. Select sensor type and calibration mode
3. Start data acquisition
4. Stop acquisition after collecting sufficient data points
5. Run calculation
6. Save calibration parameters

### Data Logging
1. Go to the "Session List" interface
2. Start a new session
3. Select an orthogonality correction file (optional)
4. The system automatically logs data to the database

## Testing

```bash
dotnet test MagnetometerSystem.sln
```

## Project Version

Current version information can be found in `src/MagnetometerSystem.App/AppVersion.cs`

## Directory Structure

- `docs/` - Project documentation
- `tests/` - Unit and integration tests
- `.claude/` - Claude AI assistant configuration
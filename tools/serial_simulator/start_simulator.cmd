@echo off
rem Open the serial simulator window. Requires Python 3.9+ and pyserial (python -m pip install pyserial).
start "" pythonw "%~dp0serial_simulator.py" %*

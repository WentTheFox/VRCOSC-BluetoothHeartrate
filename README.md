<h1><img src="BluetoothHeartrateModule/logo/logo.png" width="32"> VRCOSC Bluetooth Heartrate</h1>

A Bluetooth Low Energy (BLE) heartrate sensor module for [VRCOSC] based on the original [HeartrateModule].
While any BLE-compliant device should work in theory, I have tested this with a Polar H10.
Additionally, a community member confirmed this to be working with the Coospo H808S.

[VRCOSC]: https://github.com/VolcanicArts/VRCOSC
[HeartrateModule]: https://github.com/VolcanicArts/VRCOSC/blob/2022.1219.0/VRCOSC.Game/Modules/Modules/Heartrate/HeartRateModule.cs

## How to use

### Prerequsites

This module requires that your machine has either:
* built-in Bluetooth support
* a USB Bluetooth dongle

Either of which must be capable of handling at least Bluetooth version 4.0 (which includes support for talking to BLE devices)

### Setup steps

1. Start VRCOSC and go to the Download page (1st icon from the top)
2. Find "Bluetooth Heartrate" in the list
3. Click the dropdown that says "Not Installed", select the latest available version (starts with "v2")
4. Go to the Modules page (2nd icon from the top)
5. Enable "Bluetooth Heartrate" (by clicking on the blank square in front of it)
5. Turn on your heartrate monitor (if it has no separate power button, then simply put in on)
6. Switch to the Run page (4th "play" icon) and press the green play button to start the modules
7. Wait for the module to discover your device. You should see a dropdown appear in the runtime view showing your connected devices
10. Once you see your device in the list, click its name
11. The module will automatically start connecting

If everything went well, you should see the following log messages:

> \[Bluetooth Heartrate]: Selected device with MAC AB:CD:EF:01:23:45<br>
> \[Bluetooth Heartrate]: Connection successful<br>
> \[Bluetooth Heartrate]: Stopped scanning for devices

[Releases]: https://github.com/WentTheFox/VRCOSC-BluetoothHeartrate/releases

#### Advanced usage

There is an option to enable broadcasting the heartrate value over Websocket to enable sharing this value with external tools, like web-based streaming overlays.

You will need an attional program or website to receive and process these values, if you don't have a specific use for this, then enabling it only serves to waste your system resources.

When enabled, a Websocket server is created that will listen on the provided host and port, and it will broadcast the current heart rate value every second to all connected clients.

Usually a host value of `127.0.0.1` will be a safe bet as it allows you to connect to the server locally, but `0.0.0.0` can also be used to allow connections from external devices as well.

As for the port, you can choose any number that is not already in use on the system (for external connections, this must be forwarded on your router and/or allower though any firewalls). The default (which has no hidden meaning whatsoever) should be a relatively safe bet.

### Known issues

Sometimes Windows' Bluetooth API starts being weird, and you will see a "No heartrate characteristic found" in the logs.
This can happen either temporarily (and the module might recover on its own after a few seconds) or it can be a persistent issue.

If the device cannot be connected to, the device name shown on the dropdown will blink blue/yellow.
When this happens, try the steps below.

### Troubleshooting

If something went wrong, try:
* restarting VRCOSC
* turning Bluetooth off and back on in the system settings
* physically disconnet and reconnect the Bluetooth dongle (if it's not built in to your device)
* restarting your PC
* turning your heart rate monitor off and back on (if it's lacking a power button, take it off for a bit, then put it back on)

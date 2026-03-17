#!/bin/bash
# High-reliability setup for EchoLink Virtual Audio on Linux.

SINK_NAME="EchoLink_Virtual_Sink"
SINK_DESC="EchoLink_Virtual_Output"
SOURCE_NAME="EchoLink_Virtual_Mic"
SOURCE_DESC="EchoLink_Virtual_Mic"

echo "[EchoLink Audio] Checking virtual devices..."

# 1. Identify existing modules by name or description
EXISTING_SINK_ID=$(pactl list modules short | grep -E "sink_name=$SINK_NAME|device.description=\"$SINK_DESC\"" | cut -f1)
EXISTING_SOURCE_ID=$(pactl list modules short | grep -E "source_name=$SOURCE_NAME|device.description=\"$SOURCE_DESC\"" | cut -f1)

# Also check for old naming conventions to be thorough
OLD_IDS=$(pactl list modules short | grep -E "EchoLink_Sink|EchoLink_Source|EchoLink_Virtual_Input" | cut -f1)

# 2. If everything is already perfect, do nothing to avoid UI glitches
if [ ! -z "$EXISTING_SINK_ID" ] && [ ! -z "$EXISTING_SOURCE_ID" ]; then
    echo "[EchoLink Audio] Devices already exist and are correctly configured. (Sink ID: $EXISTING_SINK_ID, Source ID: $EXISTING_SOURCE_ID)"
    # Just ensure they are unmuted and 100% volume
    pactl set-sink-mute "$SINK_NAME" false 2>/dev/null
    pactl set-sink-volume "$SINK_NAME" 65536 2>/dev/null
    pactl set-source-mute "$SOURCE_NAME" false 2>/dev/null
    pactl set-source-volume "$SOURCE_NAME" 65536 2>/dev/null
    exit 0
fi

# 3. Clean up if we are missing part of the chain or have old devices
echo "[EchoLink Audio] Re-initializing audio chain..."
for id in $EXISTING_SINK_ID $EXISTING_SOURCE_ID $OLD_IDS; do
    if [ ! -z "$id" ]; then
        echo "Unloading module ID $id..."
        pactl unload-module "$id" 2>/dev/null
    fi
done

# 4. Create the Sink (App Playback Target)
echo "Creating virtual sink: $SINK_NAME..."
NEW_SINK_ID=$(pactl load-module module-null-sink \
    sink_name=$SINK_NAME \
    rate=48000 \
    channels=2 \
    sink_properties=device.description="$SINK_DESC")

if [ -z "$NEW_SINK_ID" ]; then
    echo "[ERROR] Failed to create virtual sink."
    exit 1
fi

# 5. Create the Source (Remapped Microphone)
echo "Creating virtual source: $SOURCE_NAME..."
NEW_SOURCE_ID=$(pactl load-module module-remap-source \
    master=$SINK_NAME.monitor \
    source_name=$SOURCE_NAME \
    rate=48000 \
    channels=2 \
    source_properties=device.description="$SOURCE_DESC")

if [ -z "$NEW_SOURCE_ID" ]; then
    echo "[ERROR] Failed to create virtual source."
    exit 1
fi

# 6. Final unmask and volume
pactl set-sink-mute "$SINK_NAME" false 2>/dev/null
pactl set-sink-volume "$SINK_NAME" 65536 2>/dev/null
pactl set-source-mute "$SOURCE_NAME" false 2>/dev/null
pactl set-source-volume "$SOURCE_NAME" 65536 2>/dev/null

echo "[EchoLink Audio] Setup complete. Sink ID: $NEW_SINK_ID, Source ID: $NEW_SOURCE_ID"

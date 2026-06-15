# Copilot Instructions

## Project Guidelines
- When investigating audio stutter, prioritize eliminating per-audio-callback allocations (e.g., replacing 'new float[floatCount]' in SeekableOpusReader.Read) and prefer reusing buffers on the audio thread to avoid GC spikes.
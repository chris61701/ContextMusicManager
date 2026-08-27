/* The MINIAUDIO_IMPLEMENTATION TU for the loopback tool. Decoding/encoding/generation are
 * off (we only need a capture device); the WASAPI backend is the whole point. */
#define MA_NO_DECODING
#define MA_NO_ENCODING
#define MA_NO_GENERATION
#define MA_NO_RESOURCE_MANAGER
#define MA_NO_NODE_GRAPH
#define MA_NO_ENGINE
#define MINIAUDIO_IMPLEMENTATION
#include "../../CMM_Decoder/third_party/miniaudio/miniaudio.h"

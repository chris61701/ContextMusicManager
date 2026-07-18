/* Audible smoke test for the v35 radio-UI SFX synth: start the real mixer device and fire
 * power-on click → two tuning-static bursts (overlapped at the 4 Hz retrigger cadence, as
 * PersonalRadio does) → power-off clunk. Build+run via build_dll.sh's toolchain:
 *   gcc -O2 -o dist/test_sfx.exe test_sfx.c -I. -Ldist -lCMM_Decoder && (cd dist && ./test_sfx.exe)
 */
#include <stdio.h>
#include <windows.h>
#include "cmm_decoder.h"

int main(void) {
    int err = 0;
    printf("abi: %d\n", CMM_Decoder_abi());
    if (cmm_mix_start(48000, 2, 4800, 9600, &err) != 0) { printf("start failed: %d\n", err); return 1; }
    /* v39 clicks: plain (body/double 0 = the v38 sound), then the dial-detent default pair. */
    printf("clicks: plain (no body, single strike)\n");
    cmm_mix_set_click_tone(3200, 0.7f, 18, 0, 1000, 0,
                           1300, 0.45f, 28, 0, 750, 0);
    cmm_mix_play_sfx(2, 0.5f); Sleep(500); cmm_mix_play_sfx(3, 0.5f); Sleep(700);
    printf("clicks: dial detent (body 0.4 @1000/750, double 0.35/0.2)\n");
    cmm_mix_set_click_tone(3200, 0.7f, 18, 0.4f, 1000, 0.35f,
                           1300, 0.45f, 28, 0.4f, 750, 0.2f);
    cmm_mix_play_sfx(2, 0.5f); Sleep(500); cmm_mix_play_sfx(3, 0.5f); Sleep(700);
    printf("clicks: hard detent (body 0.8 @1400, double 0.6)\n");
    cmm_mix_set_click_tone(4000, 0.85f, 12, 0.8f, 1400, 0.6f,
                           2000, 0.6f, 16, 0.8f, 900, 0.5f);
    cmm_mix_play_sfx(2, 0.5f); Sleep(500); cmm_mix_play_sfx(3, 0.5f); Sleep(700);
    /* v36/v37 static: the new shipped default (350-2000, crackle 0.8), keepalive + stop. */
    printf("static: shipped default (350-2000, crackle 0.8)\n");
    cmm_mix_set_static_tone(350.0f, 2000.0f, 0.8f);
    { int i; for (i = 0; i < 5; i++) { cmm_mix_play_sfx(1, 0.5f); Sleep(250); } }
    cmm_mix_stop_sfx(1); Sleep(400);
    printf("click OFF (default)\n");
    cmm_mix_set_click_tone(3200, 0.7f, 18, 0.4f, 1000, 0.35f,
                           1300, 0.45f, 28, 0.4f, 750, 0.2f);
    cmm_mix_play_sfx(3, 0.5f); Sleep(600);
    cmm_mix_stop(0.1f, 1);
    Sleep(300);
    printf("done\n");
    return 0;
}

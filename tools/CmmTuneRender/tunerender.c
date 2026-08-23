/* Renders a tune-in sweep over REAL music, through the same maths as the receiver stage in
 * cmm_mix.c. usage: sweepmp3 <in.mp3> <startSec> <out.wav> <trim> <clickAmt> <clickMax> <deemHz> <secs> */
#define DR_MP3_IMPLEMENTATION
#include "dr_mp3.h"
#include <stdio.h>
#include <stdlib.h>
#include <math.h>

static void put32(FILE*f,unsigned v){fputc(v&255,f);fputc((v>>8)&255,f);fputc((v>>16)&255,f);fputc((v>>24)&255,f);}
static void put16(FILE*f,unsigned v){fputc(v&255,f);fputc((v>>8)&255,f);}

int main(int argc, char** argv){
    if (argc < 9) { printf("args\n"); return 1; }
    const char* in = argv[1];
    double startSec = atof(argv[2]);
    const char* out = argv[3];
    const char* in2 = argc > 16 ? argv[16] : NULL;
    double startSec2 = argc > 17 ? atof(argv[17]) : 0.0;
    float leaveS = argc > 18 ? atof(argv[18]) : 1.7f;   /* seconds to fall out of the station */
    float searchS= argc > 19 ? atof(argv[19]) : 3.5f;   /* seconds sitting in the noise */
    float arriveS= argc > 20 ? atof(argv[20]) : 3.5f;   /* seconds to come back in */
    float kwOv   = argc > 21 ? atof(argv[21]) : 0.0f;   /* knee width override, 0 = leave alone */
    float lockAmt= argc > 22 ? atof(argv[22]) : 0.0f;   /* stereo pilot LATCH: 0 = the old glide */
    float stagger= argc > 23 ? atof(argv[23]) : 0.0f;   /* spread the stages across the ramp */
    float nHold  = argc > 24 ? atof(argv[24]) : 0.0f;   /* how far into the ARRIVAL the hiss lasts */
    float nFloorR= argc > 25 ? atof(argv[25]) : 0.0f;   /* residual hiss that never fully leaves */
    float lockU  = argc > 26 ? atof(argv[26]) : 0.52f;  /* WHERE in the ramp stereo latches */
    float lockClickA=argc>27 ? atof(argv[27]) : 1.0f;   /* the decoder clunk. 0 = silent latch */
    float lockMs = argc > 28 ? atof(argv[28]) : 9.0f;   /* how long the image takes to open */
    float hiF    = argc > 29 ? atof(argv[29]) : 1800.0f;/* hi-cut floor: how muffled it gets */
    float tInA   = argc > 30 ? atof(argv[30]) : 0.28f;  /* treble opens: start, as ramp position */
    float tInB   = argc > 31 ? atof(argv[31]) : 0.91f;  /* ...and finish. Narrow = a snap, not a sweep */
    float hpMax  = argc > 32 ? atof(argv[32]) : 0.0f;   /* LOW cut when the signal is gone, Hz */
    float lim2   = argc > 33 ? atof(argv[33]) : 0.0f;   /* extra squash on a weak station */
    float nHiHz  = argc > 34 ? atof(argv[34]) : 0.0f;   /* static's own top cap; 0 = off */
    float musicEq= argc > 35 ? atof(argv[35]) : 0.0f;   /* 1 = band-limit the MUSIC only */
    float trim = atof(argv[4]), clickAmt = atof(argv[5]), clickMax = atof(argv[6]);
    float deemHz = atof(argv[7]), secs = atof(argv[8]);
    float wander = atof(argv[9]), flutter = atof(argv[10]);
    float fLoHz = argc > 11 ? atof(argv[11]) : 14.0f;   /* flutter band, upper edge */
    float burst  = argc > 12 ? atof(argv[12]) : 0.0f;   /* occasional interference swells */
    float floorDb= argc > 13 ? atof(argv[13]) : -99.0f; /* how far the station sinks, NOT to zero */
    float drive  = argc > 14 ? atof(argv[14]) : 0.0f;   /* how hard it is squashed as it goes */
    float muteW  = argc > 15 ? atof(argv[15]) : 8.0f;   /* dB of SNR the muting spans */

    drmp3 mp3;
    if (!drmp3_init_file(&mp3, in, NULL)) { printf("cannot open %s\n", in); return 1; }
    unsigned sr = mp3.sampleRate, ch = mp3.channels;
    drmp3_seek_to_pcm_frame(&mp3, (drmp3_uint64)(startSec * sr));

    int n = (int)(sr * secs);
    float* pcm = (float*)malloc((size_t)n * 2 * sizeof(float));
    drmp3_uint64 got = drmp3_read_pcm_frames_f32(&mp3, (drmp3_uint64)n, pcm);
    if (ch == 1) {                        /* dr_mp3 gives us `ch` interleaved; widen mono */
        for (long long i = (long long)got - 1; i >= 0; i--) { pcm[i*2] = pcm[i*2+1] = pcm[i]; }
    }
    drmp3_uninit(&mp3);
    n = (int)got;

    /* THE STATION BEING TUNED TO. Using the same song both sides made the example unreadable --
     * you cannot tell a handover from a dropout if what returns is what left. */
    float* pcm2 = NULL;
    if (in2) {
        drmp3 m2;
        if (drmp3_init_file(&m2, in2, NULL)) {
            drmp3_seek_to_pcm_frame(&m2, (drmp3_uint64)(startSec2 * m2.sampleRate));
            pcm2 = (float*)calloc((size_t)n*2, sizeof(float));
            drmp3_uint64 g2 = drmp3_read_pcm_frames_f32(&m2, (drmp3_uint64)n, pcm2);
            if (m2.channels == 1)
                for (long long i=(long long)g2-1;i>=0;i--){ pcm2[i*2]=pcm2[i*2+1]=pcm2[i]; }
            drmp3_uninit(&m2);
        } else { puts("could not open the second track"); return 1; }
    }

    float rate = (float)sr;
    float knee = 0.45f, kw = 0.22f;
    if (kwOv > 0.001f) kw = kwOv;
    float hiFloor = 1800.0f, dull = 0.85f, mono = 1.0f;
    float deemK = 1.0f - expf(-6.2831853f * deemHz / rate);
    float clickDecay = 0.9975f;

    FILE* f = fopen(out, "wb"); unsigned data = (unsigned)n * 2 * 2;
    fwrite("RIFF",1,4,f); put32(f,36+data); fwrite("WAVEfmt ",1,8,f);
    put32(f,16); put16(f,1); put16(f,2); put32(f,sr); put32(f,sr*4); put16(f,4); put16(f,16);
    fwrite("data",1,4,f); put32(f,data);

    unsigned rng = 22222u;
    float nz1=0, nz2=0, nz3=0, clk=0; int clkNext=1; float lp0=0, lp1=0, hp0=0, hp1=0;
    /* WANDER: a slow random walk on the noise level -- what a real receiver's AGC does as the
     * signal fades in and out, and the reason interstation noise breathes instead of sitting
     * still. FLUTTER: a faster wobble on top, the multipath/picket-fencing texture. */
    float wCur=1.0f, wTgt=1.0f; int wHold=1;
    /* PILOT LOCK. A real stereo decoder does not fade into stereo -- it LATCHES, with hysteresis,
     * and clicks audibly when it does. That instant is the only genuinely abrupt thing in a tune-in,
     * and without it every reveal is a glide however steep you make it. */
    int locked=0; float lockW=0.0f, lockClk=0.0f;
    float prevU=1.0f;
    /* FLUTTER AS BAND-LIMITED NOISE, not sines. Two fixed oscillators gave the wobble a RATE, and
     * a rate is a metronome -- audibly periodic, which is exactly what "static and not random"
     * describes. Filtered noise has no period at all: it just moves. */
    float fA=0.0f, fB=0.0f;
    float kFA = 1.0f - expf(-6.2831853f * fLoHz / rate);
    float kFB = 1.0f - expf(-6.2831853f * 1.7f  / rate);
    float bCur=0.0f, bTgt=0.0f; int bHold=1;

    for (int i=0;i<n;i++){
        /* hold tuned, LEAVE, sit in the search, ARRIVE, hold tuned -- the whole event */
        float p = (float)i/(float)n;
        float tsec = (float)i/rate, u;
        float t0 = (secs - leaveS - searchS - arriveS) * 0.45f;   /* tuned, before anything */
        float t1 = t0 + leaveS, t2 = t1 + searchS, t3 = t2 + arriveS;
        if      (tsec < t0) u = 1.0f;
        else if (tsec < t1) u = 1.0f - (tsec-t0)/leaveS;
        else if (tsec < t2) u = 0.0f;
        else if (tsec < t3) u = (tsec-t2)/arriveS;
        else                u = 1.0f;

        /* Hand over at the deepest point of the search, where both stations are buried under
         * the noise -- which is exactly where the game's old voice retires and the new one is
         * added, and where the swap is least audible. */
        const float* src = (pcm2 && tsec >= t1 + searchS*0.5f) ? pcm2 : pcm;
        float ml = src[i*2], mr = src[i*2+1];

        float q = (u<=0.0005f)?0.0f:((u>=0.9995f)?1.0f:1.0f/(1.0f+expf(-(u-knee)*(4.0f/kw))));
        float snr = -4.0f + 64.0f*q;
        /* THE STATION IS BURIED, NOT REMOVED. Taking it to silence is the "music fades out"
         * event -- once it is gone there is nothing left for the noise to be ON, so the two read
         * as consecutive rather than simultaneous. Sink it to a floor and leave it there: a
         * ghost under the hiss is what a real dial between stations actually gives you.
         * And span the mute over enough SNR that the station is dropping WHILE the noise is
         * rising, instead of holding still and then stopping. */
        float lim = -floorDb;
        float mute = (lim/muteW)*(14.0f-snr); if(mute<0)mute=0; if(mute>lim)mute=lim;
        float progG = mute>0.0f ? powf(10.0f,-mute*0.05f) : 1.0f;

        if (--wHold <= 0) {
            rng = rng*1664525u+1013904223u;
            float uw = (float)(rng>>8)*(1.0f/16777216.0f);
            wTgt = 1.0f + wander*(uw*2.0f - 1.0f);       /* +/- wander around unity */
            if (wTgt < 0.05f) wTgt = 0.05f;
            rng = rng*1664525u+1013904223u;
            float uh = (float)(rng>>8)*(1.0f/16777216.0f);
            wHold = (int)(rate*(0.18f + 0.9f*uh));       /* a new target every 0.18-1.1 s */
        }
        wCur += (wTgt - wCur) * 0.00012f;                /* slew: seconds, not milliseconds */
        rng = rng*1664525u+1013904223u;
        float fw = (float)(int)rng*(1.0f/2147483648.0f);
        fA += kFA*(fw - fA);          /* keep everything below the band's top edge */
        fB += kFB*(fA - fB);          /* ...and take out the slow part, which wander owns */
        float fmod = (fA - fB) * 46.0f;
        if (fmod >  1.4f) fmod =  1.4f; else if (fmod < -1.4f) fmod = -1.4f;
        float flut = 1.0f + flutter*fmod;
        if (flut < 0.05f) flut = 0.05f;

        /* BURSTS: occasional swells of interference that arrive and pass, rather than a bed that
         * is always the same loudness. Sparse -- a couple every few seconds. */
        if (burst > 0.001f) {
            if (--bHold <= 0) {
                rng = rng*1664525u+1013904223u;
                float ub = (float)(rng>>8)*(1.0f/16777216.0f);
                bTgt = (ub < 0.45f) ? (0.5f + 1.6f*ub) * burst : 0.0f;
                rng = rng*1664525u+1013904223u;
                float uh2 = (float)(rng>>8)*(1.0f/16777216.0f);
                bHold = (int)(rate*(0.10f + 0.55f*uh2));
            }
            bCur += (bTgt - bCur) * 0.00035f;
            flut *= 1.0f + bCur;
        }

        /* THE HISS MUST OUTLAST THE TUNING. Tied to signal alone it was gone while the station
         * was still opening up, which leaves a stretch of clean-but-dull music fading in with
         * nothing over it -- and a fade with nothing over it is just a fade. On the way IN the
         * noise gets its own, later window so it is still there while the treble comes back, plus
         * a residual bed that never quite goes: a set at full lock still has a noise floor, and
         * hitting absolute zero is what says "sound effect ended" rather than "receiver". */
        int rising = (u > prevU);   /* captured BEFORE prevU is updated below */
        float nLvl = powf(10.0f,-snr*0.05f);
        if (nHold > 0.001f && rising) {
            float na = 0.22f, nb = 0.55f + 0.42f*nHold;
            float nf = 1.0f - (u-na)/(nb-na); if(nf<0)nf=0; if(nf>1)nf=1;
            nf = nf*nf*(3.0f-2.0f*nf);
            float held = nFloorR + (1.0f-nFloorR)*nf;
            if (held > nLvl) nLvl = held;
        }
        prevU = u;
        float nAmp = nLvl*trim*wCur*flut; if(nAmp>1.6f)nAmp=1.6f;
        rng = rng*1664525u+1013904223u;
        float wn = (float)(int)rng*(1.0f/2147483648.0f);
        nz1 += deemK*(wn-nz1);
        nz2 += deemK*(nz1-nz2);
        /* THE STATIC KEEPS ITS OWN TOP CAP. It used to get the shared hi-cut for free, so moving
         * that onto the music path stripped the noise of everything that was darkening it -- which
         * is why it turned harsh. It needs its own, and a fixed one: the roar between stations does
         * not brighten as a station arrives, it just goes. */
        float nzs = nz2;
        if (nHiHz > 1.0f) {
            float knh = 1.0f-expf(-6.2831853f*nHiHz/rate);
            nz3 += knh*(nz2-nz3); nzs = nz3 * 1.7f;
        }
        float nz = nzs*nAmp*2.0f;

        if (clickAmt>0.001f && snr<14.0f){
            if(--clkNext<=0){
                float r2 = clickMax*powf(10.0f,-(snr+4.0f)*0.1667f); if(r2<0.05f)r2=0.05f;
                rng = rng*1664525u+1013904223u;
                float uu=(float)(rng>>8)*(1.0f/16777216.0f); if(uu<1e-4f)uu=1e-4f;
                clkNext=(int)(-logf(uu)*rate/r2); if(clkNext<1)clkNext=1;
                rng = rng*1664525u+1013904223u;
                float sgn = ((rng>>8)&1u)?1.0f:-1.0f;
                rng = rng*1664525u+1013904223u;
                float mag = 0.4f+0.6f*((float)(rng>>8)*(1.0f/16777216.0f));
                clk = sgn*mag*clickAmt*(nAmp>0.05f?nAmp:0.05f);
            }
            clk *= clickDecay; nz += clk;
        }

        float pl = ml*progG, pr = mr*progG;
        if (drive > 0.001f) {
            /* Drive scales with how far gone the signal is, level-compensated so it dirties the
             * station rather than just making it louder. Clean music behind a blanket reads as a
             * fader; a station being mangled reads as a receiver. */
            /* UNITY SMALL-SIGNAL GAIN. The compensation used to be a hand-picked curve that
             * bore no relation to the drive, so at mid-transition the stage put 7.3x in and took
             * only 0.69x out -- the station came through FOURTEEN DECIBELS hot and then settled
             * back, which is the volume ramp. tanh(x*d)/d passes quiet parts at unity and squashes
             * loud ones, which is what a saturator is supposed to do. */
            float d = 1.0f + drive*7.0f*(1.0f - q);
            pl = tanhf(pl*d)/d; pr = tanhf(pr*d)/d;
        }
        /* THE EQ BELONGS TO THE MUSIC, NOT TO THE MIX. Putting it on the sum shaped the hiss
         * as well, which is not what a weakening station does -- the hiss has its own colour and
         * keeps it. What changes as a station goes is the PROGRAMME's bandwidth: the top rolls
         * away and the bottom goes with it, so it ends up thin and midrange-y like a small set,
         * while the noise carries on sounding like noise. */
        if (musicEq > 0.5f && u < 0.9995f) {
            float tt;
            if (stagger > 0.001f) {
                float a, b;
                if (rising) { a = tInA; b = tInB; }
                else        { a = 0.28f; b = 0.30f + 0.68f*stagger; }
                tt = 1.0f - (u - a)/(b - a);
                if (tt<0) tt=0; if (tt>1) tt=1;
                tt *= dull;
            } else { tt=(34.0f-snr)/26.0f; if(tt<0)tt=0; if(tt>1)tt=1; tt*=dull; }
            float fc = 15000.0f*powf(hiF/15000.0f,tt);
            float k = 1.0f-expf(-6.2831853f*fc/rate);
            lp0 += k*(pl-lp0); pl=lp0;
            lp1 += k*(pr-lp1); pr=lp1;
            if (hpMax > 1.0f) {
                float fhp = 20.0f + (hpMax-20.0f)*tt;
                float kh = 1.0f-expf(-6.2831853f*fhp/rate);
                hp0 += kh*(pl-hp0); pl -= hp0;
                hp1 += kh*(pr-hp1); pr -= hp1;
            }
            if (lim2 > 0.001f) {
                float d2 = 1.0f + lim2*14.0f*tt;
                pl = tanhf(pl*d2)/d2; pr = tanhf(pr*d2)/d2;
            }
        }
        float l = pl + nz, r = pr + nz*0.85f;
        if (u < 0.9995f){
            float tt;
            if (stagger > 0.001f) {
                /* THE STAGES MUST NOT ALL FIRE AT ONCE. Keyed off one number they land inside a
                 * single narrow window, which is why 2.8 s of ramp contained 0.9 s of event and
                 * 1.9 s of nothing. A real receiver recovers its treble over SECONDS while the
                 * noise has already gone -- Si47xx hi-cut releases over 3.3 s against a 3 ms
                 * attack. So give the treble its own slow window, opening well after the hiss
                 * has dropped and still opening when the station is otherwise clean. */
                /* THE WAY IN IS NOT THE WAY OUT. Both directions shared one window, and
                 * because the arrival is more than twice as long the same curve stretched
                 * into an audible LOWPASS SWEEP -- which is what 'it sounds like EQ' is.
                 * On the way out it is quick enough to read as being cut off. Coming back
                 * it needs its own window, and NARROW is what stops it sounding like
                 * somebody turning a filter knob. */
                float a, b;
                if (rising) { a = tInA; b = tInB; }
                else           { a = 0.28f; b = 0.30f + 0.68f*stagger; }
                tt = 1.0f - (u - a)/(b - a);
                if (tt<0) tt=0; if (tt>1) tt=1;
                tt *= dull;
            } else { tt=(34.0f-snr)/26.0f; if(tt<0)tt=0; if(tt>1)tt=1; tt*=dull; }
            float fc = 15000.0f*powf(hiF/15000.0f,tt);
            float k = 1.0f-expf(-6.2831853f*fc/rate);
            lp0 += k*(l-lp0); l=lp0;
            lp1 += k*(r-lp1); r=lp1;
        } else { lp0=l; lp1=r; }
        float w;
        if (lockAmt > 0.001f) {
            /* LATCH ON THE RAMP POSITION, NOT ON SNR. snr saturates near 60 by the time the
             * station is merely audible, so anything keyed to it necessarily fires early -- the
             * station arrived and was already in stereo, which is the one thing a real decoder
             * never does. It is the LAST thing to come good, well after the treble. */
            if (!locked && u > lockU) {
                locked = 1; lockClk = 0.34f*lockAmt*lockClickA;
            } else if (locked && u < lockU - 0.14f) {   /* hysteresis: harder to lose than to get */
                locked = 0; lockClk = 0.20f*lockAmt*lockClickA;
            }
            /* Fast enough to be an EVENT, slow enough not to be a step. Nine milliseconds is
             * what a real decoder does and it clicks; a few tens of ms still reads as a snap. */
            float kSnap = 1.0f - expf(-1.0f/(0.001f*lockMs*rate));
            lockW += ((locked?1.0f:0.0f) - lockW) * kSnap;
            w = lockW;
            lockClk *= 0.9986f;
            l += lockClk; r += lockClk;            /* the decoder clunking over */
        } else {
            w = (snr-14.0f)/13.0f; if(w<0)w=0; if(w>1)w=1;
        }
        w = 1.0f - mono*(1.0f-w);
        float m=0.5f*(l+r), sd=0.5f*(l-r)*w; l=m+sd; r=m-sd;

        float o[2]={l,r};
        for(int c=0;c<2;c++){
            int v=(int)(o[c]*0.55f*32767.0f);
            if(v>32767)v=32767; if(v<-32768)v=-32768;
            put16(f,(unsigned)(v&0xFFFF));
        }
    }
    fclose(f); free(pcm);
    printf("%-26s trim %.2f  clicks %.2f @ %.0f/s  deem %.0f\n", out, trim, clickAmt, clickMax, deemHz);
    return 0;
}

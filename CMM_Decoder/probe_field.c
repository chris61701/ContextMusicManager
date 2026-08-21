/* probe_field — where does the outdoors floor come from?
 * The game reports ~5.8 dB of "wall" and 3-7 dB of "detour" with a clear view of a speaker.
 * This reproduces the game's geometry (STEP 3, 0.35 dB/block detour) and takes the two apart. */
#include "cmm_field.h"
#include <stdio.h>
#include <stdlib.h>
#include <math.h>
#include <string.h>

#define W 128
#define H 48
#define STEP 3
static unsigned char solid[W][H][W];
static int FACE_RULE = 2;

static int isSolid(int x,int y,int z){
    if(x<0||x>=W||y<0||y>=H||z<0||z>=W) return 1;
    return solid[x][y][z];
}
static float rule(float mean,float best){
    if(FACE_RULE==0) return mean; if(FACE_RULE==1) return best; return sqrtf(mean*best);
}
static float faceT(int cx,int cy,int cz,int axis){
    float acc=0,best=0; int n=0;
    for(int a=0;a<STEP;a++) for(int b=0;b<STEP;b++){
        int x,y,z,px,py,pz;
        if(axis==0){x=cx*STEP;y=cy*STEP+a;z=cz*STEP+b;px=x-1;py=y;pz=z;}
        else if(axis==1){x=cx*STEP+a;y=cy*STEP;z=cz*STEP+b;px=x;py=y-1;pz=z;}
        else{x=cx*STEP+a;y=cy*STEP+b;z=cz*STEP;px=x;py=y;pz=z-1;}
        float t=(isSolid(x,y,z)||isSolid(px,py,pz))?0.f:1.f;
        acc+=t; if(t>best)best=t; n++;
    }
    return n?rule(acc/n,best):0.f;
}
static float midT(int cx,int cy,int cz,int axis){
    int h=STEP/2; float acc=0,best=0; int n=0;
    for(int a=0;a<STEP;a++) for(int b=0;b<STEP;b++){
        int x,y,z;
        if(axis==0){x=cx*STEP+h;y=cy*STEP+a;z=cz*STEP+b;}
        else if(axis==1){x=cx*STEP+a;y=cy*STEP+h;z=cz*STEP+b;}
        else{x=cx*STEP+a;y=cy*STEP+b;z=cz*STEP+h;}
        float t=isSolid(x,y,z)?0.f:1.f;
        acc+=t; if(t>best)best=t; n++;
    }
    return n?rule(acc/n,best):0.f;
}
static void fill_all(cmm_fld*f){
    int cx,cy,cz,g=0;
    while(cmm_fld_next_pending(f,&cx,&cy,&cz)&&g++<4000000)
        cmm_fld_set_cell(f,cx,cy,cz,faceT(cx,cy,cz,0),faceT(cx,cy,cz,1),faceT(cx,cy,cz,2),
                                    midT(cx,cy,cz,0), midT(cx,cy,cz,1), midT(cx,cy,cz,2));
}

static const int GY = 20;                 /* ground surface: blocks 0..GY solid */

static void scene_flat(void){
    memset(solid,0,sizeof solid);
    for(int x=0;x<W;x++) for(int z=0;z<W;z++) for(int y=0;y<=GY;y++) solid[x][y][z]=1;
}

static void run(const char*name,int speakerBlock,float earOff){
    cmm_fld*f=cmm_fld_create(W/STEP,H/STEP,W/STEP,STEP);
    cmm_fld_set_tuning(f,0.35f,60.f);
    cmm_fld_recentre(f,64,GY+2,64);
    fill_all(f);
    float ex=64.5f, ey=GY+1+earOff, ez=64.5f;
    cmm_fld_solve(f,ex,ey,ez);
    printf("\n%s (ear y=%.1f, ground top y=%d)\n",name,ey,GY);
    printf("   dist    excess     wall    detour   dB/blk\n");
    for(int d=6; d<=48; d+=6){
        float sx=64.5f+d, sy=GY+1.5f, sz=64.5f;
        if(speakerBlock){ /* the speaker is itself a solid block */
            solid[(int)sx][(int)sy][(int)sz]=1;
            cmm_fld_dirty(f,(int)sx,(int)sy,(int)sz,4);
            fill_all(f); cmm_fld_solve(f,ex,ey,ez);
        }
        float e=cmm_fld_sample_db(f,sx,sy,sz);
        float m=cmm_fld_sample_mat_db(f,sx,sy,sz);
        printf("  %5.1f   %6.2f   %6.2f   %6.2f   %6.3f\n",(float)d,e,m,e-m>0?e-m:0,e/d);
        if(speakerBlock){ solid[(int)sx][(int)sy][(int)sz]=0; cmm_fld_dirty(f,(int)sx,(int)sy,(int)sz,4); fill_all(f); }
    }
    cmm_fld_destroy(f);
}

int main(int argc,char**argv){
    if(argc>1) FACE_RULE=atoi(argv[1]);
    printf("cmm_field ABI %d  STEP %d  face rule %s\n",cmm_fld_abi(),STEP,
           FACE_RULE==0?"MEAN":FACE_RULE==1?"MAX":"BLEND");
    scene_flat();
    run("A. flat ground, ear 1.7 above surface, source 1.5 above surface",0,1.7f);
    run("B. same, but the source cell contains a solid speaker block",1,1.7f);
    scene_flat();
    run("C. floating in the air, 8 above the ground (nothing near)",0,8.0f);
    return 0;
}

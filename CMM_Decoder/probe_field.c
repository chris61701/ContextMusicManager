/* probe_field — does the field answer 0 dB in open air, wherever the ground happens to sit?
 *
 * This is the probe that found the defect the 1-block-tall cell exists to fix. The whole point is
 * the GY sweep: with cubic 3-block cells the answer in FLAT OPEN AIR swung between 0.00 and 29.42
 * dB purely with where the ground's top block fell relative to the cell grid. An earlier version
 * of this probe tested one ground height, got 0.00, and pronounced the model clean.
 */
#include "cmm_field.h"
#include <stdio.h>
#include <stdlib.h>
#include <math.h>
#include <string.h>

#define W 128
#define H 48
static int SX = 3, SY = 1, SZ = 3;
static unsigned char solid[W][H][W];
static int FACE_RULE = 2;

static int isSolid(int x,int y,int z){
    if(x<0||x>=W||y<0||y>=H||z<0||z>=W) return 1;
    return solid[x][y][z];
}
static float rule(float mean,float best){
    if(FACE_RULE==0) return mean;
    if(FACE_RULE==1) return best;
    return sqrtf(mean*best);
}

/* THE MATERIAL IN ONE HALF OF A CELL, as a linear transmission (1 = nothing there, 0 = sealed) —
 * the model cmm_field has taken since ABI 4. A move charges the half it leaves through plus the
 * half it enters through, so the pair on an axis has to compose back to the cell's own material:
 * both halves hold something => each reports sqrt(T), only one does => that one reports T.
 *
 * Kept in step with test_field.c's halfT, which is the graded one. Note this probe used to ask
 * BOTH sides of a face (`isSolid(x,y,z) || isSolid(px,py,pz)`) where the field asks only the
 * cell's own blocks — that made everything read as sealing better than it does. Own cell only. */
static float halfT(int cx,int cy,int cz,int axis,int positive){
    int step = axis==0?SX:axis==1?SY:SZ;
    int nHi = (step+1)/2 - 1, pLo = step - (step+1)/2;
    int bx=cx*SX, by=cy*SY, bz=cz*SZ;
    int ea = axis==0?SY:SX, eb = axis==2?SY:SZ;
    int nCol=0, blkNeg=0, blkPos=0, blkAny=0;
    for(int a=0;a<ea;a++) for(int b=0;b<eb;b++){
        int hitN=0, hitP=0;
        for(int d=0;d<step;d++){
            int x,y,z;
            if(axis==0){ x=bx+d; y=by+a; z=bz+b; }
            else if(axis==1){ x=bx+a; y=by+d; z=bz+b; }
            else { x=bx+a; y=by+b; z=bz+d; }
            if(isSolid(x,y,z)){ if(d<=nHi) hitN=1; if(d>=pLo) hitP=1; }
        }
        nCol++; blkNeg+=hitN; blkPos+=hitP; if(hitN||hitP) blkAny++;
    }
    if(!nCol) return 0.f;
    int mine = positive?blkPos:blkNeg, other = positive?blkNeg:blkPos;
    if(!mine) return 1.f;
    float T = rule((float)(nCol-blkAny)/(float)nCol, blkAny<nCol?1.f:0.f);
    return other ? sqrtf(T) : T;
}

static void fill_all(cmm_fld*f){
    int cx,cy,cz,g=0;
    while(cmm_fld_next_pending(f,&cx,&cy,&cz)&&g++<8000000)
        cmm_fld_set_cell(f,cx,cy,cz,
                         halfT(cx,cy,cz,0,0), halfT(cx,cy,cz,1,0), halfT(cx,cy,cz,2,0),
                         halfT(cx,cy,cz,0,1), halfT(cx,cy,cz,1,1), halfT(cx,cy,cz,2,1));
}

static int GY = 20;

static void scene_flat(void){
    memset(solid,0,sizeof solid);
    for(int x=0;x<W;x++) for(int z=0;z<W;z++) for(int y=0;y<=GY;y++) solid[x][y][z]=1;
}

/* Worst open-air excess over a sweep of distances — one number per scene, so the GY sweep fits
 * on a screen and a regression is obvious rather than buried in a table. */
static float open_air_worst(float *wallOut){
    scene_flat();
    cmm_fld *f=cmm_fld_create(W/SX,H/SY,W/SZ,SX,SY,SZ);
    cmm_fld_set_tuning(f,0.35f,60.f);
    cmm_fld_recentre(f,64,GY+2,64);
    fill_all(f);
    float ex=64.5f, ey=GY+1+1.7f, ez=64.5f;
    cmm_fld_solve(f,ex,ey,ez);
    float worst=0, worstMat=0;
    for(int d=6; d<=48; d+=6){
        float sx=64.5f+d, sy=GY+1.5f, sz=64.5f;
        float e=cmm_fld_sample_db(f,sx,sy,sz);
        if(e>worst){ worst=e; worstMat=cmm_fld_sample_mat_db(f,sx,sy,sz); }
    }
    cmm_fld_destroy(f);
    if(wallOut) *wallOut=worstMat;
    return worst;
}

/* A STOREY: floor, three blocks of headroom, ceiling, with listener and source both inside it and
 * clear line of sight. This is where the music actually is, and it is the case the open-air sweep
 * cannot see. A cell tall enough to hold both floor and air reports the whole layer as part-solid,
 * so every horizontal step through the room pays for the floor under your feet -- a fog with no
 * visible cause, and the reason a room and the far side of its wall sound the same. */
static float storey_worst(int fy, float *wallOut){
    memset(solid,0,sizeof solid);
    for(int x=0;x<W;x++) for(int z=0;z<W;z++){
        for(int y=0;y<=fy;y++) solid[x][y][z]=1;      /* ground up to and including the floor */
        solid[x][fy+4][z]=1;                          /* ceiling, three blocks of headroom */
    }
    cmm_fld *f=cmm_fld_create(W/SX,H/SY,W/SZ,SX,SY,SZ);
    cmm_fld_set_tuning(f,0.35f,60.f);
    cmm_fld_recentre(f,64,fy+2,64);
    fill_all(f);
    cmm_fld_solve(f,64.5f,fy+1+1.7f,64.5f);
    float worst=0,worstMat=0;
    for(int d=6; d<=36; d+=6){
        float e=cmm_fld_sample_db(f,64.5f+d,fy+1.5f,64.5f);
        if(e>worst){ worst=e; worstMat=cmm_fld_sample_mat_db(f,64.5f+d,fy+1.5f,64.5f); }
    }
    cmm_fld_destroy(f);
    if(wallOut)*wallOut=worstMat;
    return worst;
}

int main(int argc,char**argv){
    if(argc>1) FACE_RULE=atoi(argv[1]);
    if(argc>2){ SY=atoi(argv[2]); }
    printf("cmm_field ABI %d   cells %dx%dx%d blocks   face rule %s\n",
           cmm_fld_abi(), SX,SY,SZ, FACE_RULE==0?"MEAN":FACE_RULE==1?"MAX":"BLEND");
    printf("\nFLAT OPEN AIR, nothing between listener and source. Want 0.00 at every ground height:\n");
    printf("  ground top y | worst excess over d=6..48 |  of which wall\n");
    float worstAll=0;
    for(GY=18; GY<=25; GY++){
        float wall=0, e=open_air_worst(&wall);
        printf("      %2d       |        %7.2f dB        |    %6.2f dB %s\n",
               GY, e, wall, e>1.0f?"   <-- WRONG":"");
        if(e>worstAll) worstAll=e;
    }
    printf("\nworst across all ground heights: %.2f dB  => %s\n",
           worstAll, worstAll<=1.0f ? "PASS" : "FAIL");

    printf("\nINSIDE A STOREY (floor + 3 headroom + ceiling), clear line of sight. Want 0.00:\n");
    printf("  floor top y  | worst excess over d=6..36 |  of which wall\n");
    float worstRoom=0;
    for(int fy=18; fy<=25; fy++){
        float wall=0, ee=storey_worst(fy,&wall);
        printf("      %2d       |        %7.2f dB        |    %6.2f dB %s\n",
               fy, ee, wall, ee>1.0f?"   <-- WRONG":"");
        if(ee>worstRoom) worstRoom=ee;
    }
    printf("\nworst indoors: %.2f dB  => %s\n", worstRoom, worstRoom<=1.0f?"PASS":"FAIL");
    return (worstAll<=1.0f && worstRoom<=1.0f) ? 0 : 1;
}

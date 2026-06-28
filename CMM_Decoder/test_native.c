#include <stdio.h>
#include <stdlib.h>
#include "CMM_Decoder.h"
int main(int argc, char** argv){ setvbuf(stdout,NULL,_IONBF,0);
    FILE* f=fopen(argv[1],"rb");
    if(!f){printf("open fail\n");return 1;}
    fseek(f,0,SEEK_END); long n=ftell(f); fseek(f,0,SEEK_SET);
    unsigned char* buf=malloc(n);
    fread(buf,1,n,f); fclose(f);
    printf("file=%ld bytes, abi=%d\n", n, CMM_Decoder_abi());
    int ch=0,sr=0,err=0; long long tot=0;
    cmm_decoder* d=cmm_opus_open_memory(buf,n,&ch,&sr,&tot,&err);
    if(!d){printf("open_memory failed err=%d\n",err);return 2;}
    printf("opened ch=%d sr=%d totalFrames=%lld\n",ch,sr,tot);
    float* out=malloc(sizeof(float)*5760*ch);
    int got=cmm_opus_read_float(d,out,5760);
    printf("first read frames=%d  sample[0]=%f\n",got, got>0?out[0]:0.0f);
    long long totalRead=got;
    while((got=cmm_opus_read_float(d,out,5760))>0) totalRead+=got;
    printf("total frames read=%lld (last got=%d)\n",totalRead,got);
    cmm_opus_close(d);
    printf("OK\n");
    return 0;
}

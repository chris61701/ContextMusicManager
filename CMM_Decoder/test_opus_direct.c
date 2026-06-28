#include <stdio.h>
#include <opus.h>
int main(){
  setvbuf(stdout,NULL,_IONBF,0);
  printf("get_size(2)=%d\n", opus_decoder_get_size(2));
  int err=0; OpusDecoder* d = opus_decoder_create(48000,2,&err);
  printf("create err=%d ptr=%p\n", err, (void*)d);
  if(d) opus_decoder_destroy(d);
  printf("DIRECT OK\n"); return 0;
}

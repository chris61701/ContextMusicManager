# Octave-band energy of a click, via a real FFT.
# The first version of this decimated by 4 inside the DFT loop "for speed", which is aliasing,
# not decimation -- it reported two thirds of the energy above 7 kHz for every setting including
# ones that could not possibly produce it, and sent me chasing a spectral hole that was an
# artefact of the measurement. Third time in this task that a measure I wrote myself was the
# thing that was wrong.
import wave, struct, sys
import numpy as np
w=wave.open(sys.argv[1]); n=w.getnframes(); r=w.getframerate()
d=np.frombuffer(w.readframes(n), dtype='<i2').astype(float)/32768.0; w.close()
on=int(np.argmax(np.abs(d)>1e-4))
seg=d[on:on+4096]
if len(seg)<4096: seg=np.pad(seg,(0,4096-len(seg)))
# NO WINDOW. A click is a decaying transient that starts at sample 0 and is over within a few
# hundred; a Hanning window peaks at the MIDDLE of the buffer, so it multiplied the entire signal
# by the near-zero rising edge and I was measuring the window rather than the sound. The signal
# already decays to silence inside the buffer, which is precisely the condition under which no
# window is needed.
S=np.abs(np.fft.rfft(seg))**2
f=np.fft.rfftfreq(len(seg), 1.0/r)
B=[("200-500",200,500),("0.5-1.2k",500,1200),("1.2-3k",1200,3000),("3-7k",3000,7000),("7-16k",7000,16000)]
v=[S[(f>=a)&(f<b)].sum() for _,a,b in B]; t=sum(v) or 1e-9
cen=(S*f).sum()/max(S.sum(),1e-12)
print("  %-28s %s  centroid %4.0f Hz  peak %.2f"
      % (sys.argv[2] if len(sys.argv)>2 else sys.argv[1],
         " ".join("%s=%2.0f%%"%(B[i][0],100*v[i]/t) for i in range(5)), cen,
         float(np.abs(d[on:]).max())))

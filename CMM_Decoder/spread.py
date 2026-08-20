import wave, struct, math, sys
w=wave.open('click_dial_spin.wav'); n=w.getnframes(); r=w.getframerate()
d=[x/32768.0 for x in struct.unpack('<%dh'%n, w.readframes(n))]; w.close()
out=[]
for i in range(8):
    a=int((0.10+i*0.16)*r); seg=d[a:a+int(0.15*r)]
    win=int(r*0.001)
    env=[math.sqrt(sum(x*x for x in seg[j:j+win])/win) for j in range(0,len(seg)-win,win)]
    e=max(env) or 1e-9
    out.append(next((j for j,v in enumerate(env) if v<e*0.01), len(env)))
print("   %s  ->  %d-%d ms" % (sys.argv[1], min(out), max(out)), out)

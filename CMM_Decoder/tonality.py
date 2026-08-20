import wave, struct, sys, math
# Tonality = the strongest NORMALISED autocorrelation at a lag the ear could hear as a PITCH
# (80..1600 Hz). The first version searched down to 8 samples / 6 kHz, where any low-passed
# signal correlates strongly with itself — it was measuring dullness, not pitch, and it said the
# fix had not worked when what it was reading was the tone cap.
def tonality(path):
    w=wave.open(path); n=w.getnframes(); r=w.getframerate()
    d=[x/32768.0 for x in struct.unpack('<%dh'%n, w.readframes(n))]; w.close()
    on=next(i for i,x in enumerate(d) if abs(x)>1e-4)
    d=d[on:on+int(r*0.06)]
    best=(0.0,0)
    for lag in range(int(r/1600), int(r/80)):
        m=len(d)-lag
        if m < r*0.005: break
        num=sum(d[i]*d[i+lag] for i in range(m))
        e1=sum(d[i]*d[i] for i in range(m))
        e2=sum(d[i+lag]*d[i+lag] for i in range(m))
        den=math.sqrt(e1*e2) or 1e-12
        v=num/den
        if v>best[0]: best=(v,lag)
    pk=max(abs(x) for x in d)
    return best[0], (r/best[1] if best[1] else 0), pk
for f in sys.argv[1:]:
    t,hz,pk = tonality(f)
    print("  %-22s tonality %.3f  (ringing at %4.0f Hz)   peak %.2f" % (f, t, hz, pk))

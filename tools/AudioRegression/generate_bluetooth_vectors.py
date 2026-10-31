"""Regenerate the shared Bluetooth golden vectors from the repository root.
Python reference encoder/decoder values are checked by Kotlin and C# tests.
"""
import math,json,struct
steps=[7,8,9,10,11,12,13,14,16,17,19,21,23,25,28,31,34,37,41,45,50,55,60,66,73,80,88,97,107,118,130,143,157,173,190,209,230,253,279,307,337,371,408,449,494,544,598,658,724,796,876,963,1060,1166,1282,1411,1552,1707,1878,2066,2272,2499,2749,3024,3327,3660,4026,4428,4871,5358,5894,6484,7132,7845,8630,9493,10442,11487,12635,13899,15289,16818,18500,20350,22385,24623,27086,29794,32767]
vs=[]
for stream in [0,1]:
 p=[0,0];ix=[40,40]
 for seq in range(2):
  samples=[]
  for frame in range(240):
   for ch in range(2): samples.append(int(10000*math.sin(2*math.pi*(997 if ch==0 else 613)*(seq*240+frame)/48000)))
  header=struct.pack('<4sBBHIII',b'BAS2',stream,0,0,1234,seq,seq*5000)
  channels=1 if stream else 2; stride=3 if stream else 2
  enc=bytearray(header+bytes([channels,stride,0,0]))
  for c in range(channels):enc+=struct.pack('<hBB',p[c],ix[c],0)
  nibbles=[];decoded=[]
  for frame in range(0,240,stride):
   for ch in range(channels):
    value=int(sum(samples[(frame+j)*2+ch] for j in range(stride))/stride)
    delta=value-p[ch];rem=abs(delta);code=8 if delta<0 else 0;step=steps[ix[ch]];change=step>>3
    for bit in [2,1,0]:
     v=step>>(2-bit)
     if rem>=v:code|=1<<bit;rem-=v;change+=v
    p[ch]=max(-32768,min(32767,p[ch]+(-change if code&8 else change)));ix[ch]=max(0,min(88,ix[ch]+[-1,-1,-1,-1,2,4,6,8][code&7]));nibbles.append(code);decoded.append(p[ch])
  enc+=bytes(nibbles[i]|nibbles[i+1]<<4 for i in range(0,len(nibbles),2))
  if stream:decoded=[x for x in decoded for _ in range(6)]
  else:decoded=[x for i in range(0,len(decoded),2) for _ in range(2) for x in decoded[i:i+2]]
  out=header+struct.pack('<'+'h'*len(decoded),*decoded)
  vs.append(dict(stream=stream,sequence=seq,pcm=(header+struct.pack('<'+'h'*len(samples),*samples)).hex(),encoded=enc.hex(),decoded=out.hex()))
json.dump(vs,open('testdata/bluetooth-adpcm.json','w'),indent=2)

'use strict';
// Only a hashed identity crosses the internal network; credentials stay in the API.
const lastAccepted=new Map();
async function notifyExpired(key, request=fetch, now=Date.now()) {
 if(!/^[a-f0-9]{64}$/.test(key))throw Error('Invalid session key');
 if(lastAccepted.has(key)&&now-lastAccepted.get(key)<600000)return false;
 const response=await request('http://api:5000/internal/gib/session-expired',{method:'POST',headers:{'Content-Type':'application/json','X-Robot-Token':process.env.ROBOT_TOKEN||''},body:JSON.stringify({clientKey:key}),redirect:'error',signal:AbortSignal.timeout(5000)});
 if(response.status!==202)throw Error('Session renewal event HTTP '+response.status);
 lastAccepted.set(key,now);
 console.log('[keepalive] otomatik giriş olayı kabul edildi — oturum:'+key.slice(0,8));
 return true;
}
module.exports={notifyExpired};

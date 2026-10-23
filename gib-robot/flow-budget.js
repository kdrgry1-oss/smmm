'use strict';
const {AsyncLocalStorage}=require('node:async_hooks');
const scopes=new AsyncLocalStorage();
function track(browser){const scope=scopes.getStore();if(scope)scope.add(browser);}
async function bounded(work,ms){
 const browsers=new Set();let timer;
 const task=scopes.run(browsers,()=>Promise.resolve().then(work));
 const timeout=new Promise((_,reject)=>{timer=setTimeout(()=>{const e=new Error('GİB işlemi süre sınırını aştı; açık tarayıcılar kapatıldı.');e.timedOut=true;reject(e)},ms)});
 try{return await Promise.race([task,timeout]);}
 finally{
  clearTimeout(timer);
  await Promise.allSettled([...browsers].map(b=>b.close()));
  // Keep the caller's account lock until its work has actually stopped.
  await task.catch(()=>{});
 }
}
module.exports={track,bounded};

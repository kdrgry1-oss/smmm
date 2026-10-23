'use strict';
const {createHash}=require('crypto');
const session=require('./session');
const {ranges}=require('./earsiv-incoming');
const BASE='https://dijital.gib.gov.tr/apigateway/eislemler/earsiv/alici-list';
const fmt=n=>new Date(n).toISOString().slice(0,10).split('-').reverse().join('/');
function amount(v){if(v===null||v===undefined||v===''||!Number.isFinite(Number(v)))throw Error('GİB fatura tutarı eksik veya geçersiz.');return Number(v);}
function mapRow(r,identity){
 const issueDate=String(r.duzenlenmeTarihi||'').slice(0,10);
 if(!/^\d{4}-\d{2}-\d{2}$/.test(issueDate)||!r.faturaNo||!r.tcknVkn||!r.unvan||!r.paraBirimi)throw Error('GİB fatura alanları eksik; aktarım durduruldu.');
 // This source exposes no ETTN. Stable source ID, never presented as a legal ETTN.
 const uuid='DVD-'+createHash('sha256').update(JSON.stringify([identity,r.tcknVkn,r.faturaNo])).digest('hex').slice(0,60);
 return {uuid,documentNumber:r.faturaNo,issueDate,senderTaxId:r.tcknVkn,senderTitle:r.unvan,taxExclusiveAmount:amount(r.toplamTutar),taxTotal:amount(r.vergilerTutari),payableAmount:amount(r.odenecekTutar),currency:r.paraBirimi,documentType:'e-Arşiv Fatura (DVD özet)'};
}
async function fetchIncoming(args,request=fetch,store=session){
 let token;
 const key=args.username;
 try{
  const periods=ranges(args.from,args.to);
  if(args.sessionKey||args.targetTaxId||!key)return {ok:false,retryable:false,items:[],message:'GİB fatura sorgusu mükellefin kendi oturumuyla yapılmalıdır.'};
  const state=store.loadState(key);token=state?.__sessionStorage?.token;
  if(!token)throw Object.assign(Error('GİB fatura oturumu yenilenmeli.'),{renew:true});
  const headers={'Content-Type':'application/json',Authorization:'Bearer '+token};
  const cookies=(state.cookies||[]).filter(c=>['dijital.gib.gov.tr','.gib.gov.tr','.dijital.gib.gov.tr'].includes(c.domain)).map(c=>c.name+'='+c.value).join('; ');
  if(cookies)headers.Cookie=cookies;
  const budget=AbortSignal.timeout(150000),items=new Map();
  for(const [a,b] of periods){
   let expected;
   for(let pageNo=1;pageNo<=1000;pageNo++){
    const res=await request(BASE,{method:'POST',headers,redirect:'error',signal:AbortSignal.any([budget,AbortSignal.timeout(30000)]),body:JSON.stringify({meta:{pagination:{pageNo,pageSize:50},sortFieldName:'faturaNo',sortType:'DESC',filters:[]},data:{duzenlenmeTarihiBas:fmt(a),duzenlenmeTarihiSon:fmt(b)}})});
    if(res.status===401||res.status===403)throw Object.assign(Error('GİB fatura oturumu yenilenmeli.'),{renew:true});
    const j=await res.json();
    if(res.status===422&&pageNo===1&&j.messages?.length===1&&j.messages[0].type==='INFO'&&j.messages[0].text==='e-Arşiv Faturanız bulunmamaktadır.')break;
    if(!res.ok||j.messages?.some(m=>m.type==='ERROR'))throw Error('GİB fatura sorgusu başarısız (HTTP '+res.status+').');
    const rows=j.resultListDenormalized,total=j.pageDetail?.total;
    if(!Array.isArray(rows)||!Number.isInteger(total)||total<0||j.pageDetail?.pageNo!==pageNo)throw Error('GİB fatura sayfalama yanıtı doğrulanamadı.');
    if(expected!==undefined&&expected!==total)throw Error('GİB fatura listesi sorgu sırasında değişti; tekrar deneyin.');expected=total;
    if(rows.length!==Math.min(50,Math.max(0,total-(pageNo-1)*50)))throw Error('GİB eksik fatura sayfası döndürdü.');
    for(const row of rows){const item=mapRow(row,key);const day=Date.parse(item.issueDate+'T00:00:00Z');if(day<a||day>b)throw Error('GİB sorgu aralığı dışında fatura döndürdü.');items.set(item.uuid,item);}
    if(pageNo*50>=total)break;
    if(pageNo===1000)throw Error('GİB fatura sayfa sınırı aşıldı; eksik aktarım yapılmadı.');
   }
  }
  return {ok:true,items:[...items.values()],message:`${items.size} fatura alındı. Uyarı: Kaynak Dijital Vergi Dairesi / e-Arşiv Faturalarım. Tutarlar GİB özetidir; kalemler, PDF/XML ve ayrı e-Fatura gelen kutusu bu sorguda yer almaz.`};
 }catch(e){if(e.renew&&token&&store.loadState(key)?.__sessionStorage?.token===token)store.markExpired(key);return {ok:false,items:[],retryable:false,needsCaptchaLogin:!!e.renew,requiresManual:!!e.renew,message:e.message?.startsWith('GİB')?e.message:'GİB fatura sorgusu tamamlanamadı; hiçbir kısmi kayıt aktarılmadı.'};}
}
module.exports={fetchIncoming,mapRow};

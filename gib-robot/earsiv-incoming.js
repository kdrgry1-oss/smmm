'use strict';
const {randomUUID}=require('crypto');
const HOST='https://earsivportal.efatura.gov.tr';
const DAY=86400000;
function date(s){if(!/^\d{4}-\d{2}-\d{2}$/.test(s))throw Error('Tarih biçimi geçersiz.');const d=new Date(s+'T00:00:00Z');if(!Number.isFinite(+d)||d.toISOString().slice(0,10)!==s)throw Error('Tarih geçersiz.');return +d;}
function ranges(from,to){const a=date(from),b=date(to);if(b<a||b-a>365*DAY)throw Error('En fazla bir yıllık tarih aralığı seçin.');const out=[];for(let x=a;x<=b;x+=7*DAY)out.push([x,Math.min(b,x+6*DAY)]);return out;}
function tr(d){return new Date(d).toISOString().slice(0,10).split('-').reverse().join('/');}
function pick(r,keys){for(const k of keys)if(r[k]!==undefined&&r[k]!==null&&r[k]!=='')return r[k];return null;}
function amount(v){if(typeof v==='number'&&Number.isFinite(v))return v;if(typeof v!=='string')throw Error('Fatura tutar alanı eksik.');let s=v.trim();if(s.includes(','))s=s.replace(/\./g,'').replace(',','.');if(!/^-?\d+(\.\d+)?$/.test(s))throw Error('Fatura tutarı tanınamadı.');return Number(s);}
function mapRow(r){
 const type=String(pick(r,['belgeTuru','belgeTipi'])||'');
 if(!/fatura/i.test(type))throw Error('GİB belge türü fatura olarak doğrulanamadı; kayıt aktarılmadı.');
 const uuid=pick(r,['ettn','uuid']),no=pick(r,['belgeNumarasi','faturaNo','belgeNo']);
 const rawDate=String(pick(r,['belgeTarihi','duzenlenmeTarihi','duzenlemeTarihi','tarih'])||'');
 const m=rawDate.match(/^(\d{2})[/.](\d{2})[/.](\d{4})/);const issueDate=m?`${m[3]}-${m[2]}-${m[1]}`:rawDate.slice(0,10);
 if(!uuid||!no)throw Error('GİB gelen belge alanları tanınamadı; eksik kayıt aktarılmadı.');date(issueDate);
 const payableAmount=amount(pick(r,['odenecekTutar','toplamTutar','vergilerDahilToplamTutar']));
 const taxTotal=amount(pick(r,['vergilerToplami','hesaplananKdv','toplamKdv','kdvTutari']));
 const senderTaxId=String(pick(r,['saticiVknTckn','duzenleyenVknTckn','vknTckn'])||'');
 const senderTitle=String(pick(r,['saticiUnvanAdSoyad','duzenleyenUnvanAdSoyad','unvanAdSoyad'])||'');
 if(!senderTaxId||!senderTitle)throw Error('GİB gönderen bilgileri eksik; kayıt aktarılmadı.');
 return {uuid:String(uuid),documentNumber:String(no),issueDate,senderTaxId,senderTitle,payableAmount,taxTotal,taxExclusiveAmount:Math.round((payableAmount-taxTotal)*100)/100,currency:String(pick(r,['paraBirimi'])||'TRY'),documentType:'e-Arşiv Fatura'};
}
async function fetchIncoming(args,request=fetch){
 try{
  const periods=ranges(args.from,args.to);
  if(args.sessionKey||args.targetTaxId||!args.username||!args.password)return {ok:false,retryable:false,items:[],message:'e-Arşiv gelen belge sorgusu için mükellefin kendi GİB kullanıcı bilgileri gereklidir. Müşavir tebligat oturumu bu sorguda kullanılamaz.'};
  const budget=AbortSignal.timeout(150000);
  async function post(path,body){const res=await request(HOST+path,{method:'POST',headers:{'Content-Type':'application/x-www-form-urlencoded; charset=UTF-8'},body:new URLSearchParams(body),redirect:'error',signal:AbortSignal.any([budget,AbortSignal.timeout(30000)])});if(!res.ok)throw Error('GİB e-Arşiv servisi HTTP '+res.status+' döndürdü.');const j=await res.json();if(j.error||j.data?.hata||j.messages?.some(x=>x.type==='ERROR'))throw Error('GİB e-Arşiv isteği reddedildi. Kullanıcı bilgilerini ve portal erişimini kontrol edin.');return j;}
  const login=await post('/earsiv-services/assos-login',{assoscmd:'anologin',rtype:'json',userid:args.username,sifre:args.password,sifre2:args.password,parola:'1'});
  if(!login.token)throw Error('GİB e-Arşiv oturumu açılamadı.');
  const records=new Map();
  async function list(a,b){const j=await post('/earsiv-services/dispatch',{callid:randomUUID(),token:login.token,cmd:'EARSIV_PORTAL_ADIMA_KESILEN_BELGELERI_GETIR',pageName:'RG_ALICI_TASLAKLAR',jp:JSON.stringify({baslangic:tr(a),bitis:tr(b),hourlySearchInterval:'NONE'})});if(!Array.isArray(j.data))throw Error('GİB gelen belge listesi beklenen biçimde değil.');
   if(j.data.length>=50){if(a===b)throw Error('GİB günlük sonuç sınırına ulaşıldı; eksik aktarım yapılmadı.');for(let x=a;x<=b;x+=DAY)await list(x,x);return;}
   for(const row of j.data){const r=mapRow(row);const d=date(r.issueDate);if(d<a||d>b)throw Error('GİB tarih aralığı dışında belge döndürdü; aktarım durduruldu.');records.set(r.uuid,r);}
  }
  for(const [a,b] of periods)await list(a,b);
  return {ok:true,items:[...records.values()],message:`${records.size} gelen e-Arşiv belgesi alındı. Uyarı: Kaynak e-Arşiv Portalı / Adıma Düzenlenen Belgeler; e-Fatura gelen kutusunu ve belge PDF/XML içeriğini kapsamaz.`};
 }catch(e){return {ok:false,retryable:false,items:[],message:e.name==='TimeoutError'?'GİB e-Arşiv sorgusu zaman aşımına uğradı.':e.message?.startsWith('GİB')||e.message?.startsWith('Fatura')||e.message?.startsWith('Tarih')||e.message?.startsWith('En fazla')?e.message:'GİB e-Arşiv gelen belgeleri alınamadı; tekrar deneyin.'};}
}
module.exports={fetchIncoming,ranges,mapRow};

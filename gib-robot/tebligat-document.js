"use strict";
const session = require('./session');
const { spawnSync } = require('child_process');
const BASE = 'https://dijital.gib.gov.tr/apigateway/etebligat/etebligat/';
const MAX = 20 * 1024 * 1024;
async function limited(res) {
  if (!res.ok) { const e = new Error(res.status === 401 ? 'GİB oturumu yenilenmeli.' : res.status === 403 ? 'GİB bu mükellefin mevcut oturumuna belge erişim izni vermedi (403). GİB e-Tebligat erişimini ve doğru mükellef oturumunu kontrol edin.' : 'GİB belge isteği başarısız (HTTP '+res.status+').'); e.needsSessionRenewal = res.status === 401 || res.status === 403; throw e; }
  const chunks=[]; let size=0;
  for await (const chunk of res.body) { size+=chunk.length; if(size>MAX) throw new Error('Belge boyutu sınırı aşıldı.'); chunks.push(chunk); }
  return Buffer.concat(chunks);
}
async function fetchDocument({sessionKey, externalId}) {
  let originalToken;
  try {
    if(typeof sessionKey!=='string'||sessionKey.length>200||typeof externalId!=='string'||externalId.length>200) throw new Error('Belge isteği geçersiz.');
    const state=session.loadState(sessionKey);
    const token=state?.__sessionStorage?.token;
    originalToken=token;
    if(!token) throw Object.assign(new Error('GİB oturumu yenilenmeli.'),{needsSessionRenewal:true});
    const headers={'Content-Type':'application/json',Authorization:'Bearer '+token};
    const cookies=(state.cookies||[]).filter(c=>c.domain==='dijital.gib.gov.tr'||c.domain==='.gib.gov.tr'||c.domain==='.dijital.gib.gov.tr').map(c=>c.name+'='+c.value).join('; ');
    if(cookies) headers.Cookie=cookies;
    async function post(name,data) {
      const res=await fetch(BASE+name,{method:'POST',headers,body:JSON.stringify(data),redirect:'error',signal:AbortSignal.timeout(20000)});
      return JSON.parse((await limited(res)).toString());
    }
    let dto;
    for(const arsivDurum of ['0','1']) {
      for(let pageNo=1;pageNo<=20;pageNo++) {
        const j=await post('tebligat-listele',{meta:{pagination:{pageNo,pageSize:50},sortFieldName:'id',sortType:'ASC',filters:[{fieldName:'arsivDurum',values:[arsivDurum]}]}});
        const rows=j.data?.tebligatDtoList||[];
        dto=rows.find(x=>String(x.tebligId)===externalId);
        if(dto||!rows.length||pageNo*50>=Number(j.data?.count||0))break;
      }
      if(dto)break;
    }
    if(!dto) throw new Error('Bu tebligat mevcut GİB oturumunda bulunamadı. Doğru mükellefin bağlantısını kontrol edin.');
    const detail=await post('zarf-detay-sorgula',{tarafId:dto.tarafId,tarafSecureId:dto.tarafSecureId});
    const list=await post('belge-ek-listele',{tebligId:dto.tebligId,tebligSecureId:dto.tebligSecureId});
    const belge=list.tebligBelge||list.data?.tebligBelge;
    if(!belge) throw new Error('GİB asıl belge bilgisini döndürmedi.');
    const d=detail.data||dto;
    const report=await post('belge-getir',{data:{id:belge.id,secureId:belge.secureId,belgeTip:belge.belgeTip,tarafId:d.tarafId||dto.tarafId,tarafSecureId:d.tarafSecureId||dto.tarafSecureId,uzanti:belge.uzanti,belgeAdi:belge.adi}});
    const link=report.reportLink||report.data?.reportLink;
    if(!link)throw new Error('GİB belge indirme bağlantısını döndürmedi.');
    const url=new URL(link,'https://dijital.gib.gov.tr');
    if(url.protocol!=='https:'||url.hostname!=='dijital.gib.gov.tr'||url.port||url.username||url.password)throw new Error('GİB belge bağlantısının adresi doğrulanamadı.');
    let pdf=await limited(await fetch(url,{headers:{Authorization:headers.Authorization,...(cookies?{Cookie:cookies}:{})},redirect:'error',signal:AbortSignal.timeout(30000)}));
    // GİB can name a CMS SignedData envelope .pdf. Verify the embedded
    // signature and extract its content; certificate trust is not assessed here.
    if(pdf.subarray(0,5).toString()!=='%PDF-' && pdf[0]===0x30) {
      const decoded=spawnSync('openssl',['cms','-verify','-noverify','-binary','-inform','DER'],{
        input:pdf,maxBuffer:MAX,timeout:10000,windowsHide:true,
      });
      if(decoded.status===0 && !decoded.error && decoded.stdout?.length<=MAX) pdf=decoded.stdout;
    }
    if(pdf.subarray(0,5).toString()!=='%PDF-')throw new Error('GİB yanıtı PDF değil; belge gösterilemedi.');
    return {ok:true,base64:pdf.toString('base64')};
  }catch(e){if(e.needsSessionRenewal && originalToken && session.loadState(sessionKey)?.__sessionStorage?.token===originalToken) session.markExpired(sessionKey); return {ok:false,needsSessionRenewal:e.needsSessionRenewal===true,message:e.message?.startsWith('GİB')||e.message?.startsWith('Bu tebligat')?e.message:'Asıl belge GİB üzerinden alınamadı. Tekrar deneyin.'};}
}
module.exports={fetchDocument};

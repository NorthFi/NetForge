// root/selfhost/web/lifecycle.js
'use strict';
async function lifecycleConsole(el){
 const admin=role==='admin';
 el.insertAdjacentHTML('beforeend','<div class="toolbar"><div><h1>Equipment lifecycle & RMA</h1><p class="sub">Recovery · quarantine · testing · supplier RMA · release</p></div><button id="life-refresh">Refresh</button></div><p class="note">Recovered devices are excluded from active equipment. Quarantine and RMA are not available warehouse stock. Release or disposal requires independent administrator confirmation after testing.</p><div class="buttons"><select id="life-view"><option value="recoveries">Recovery cases</option><option value="eligible">Active serialized equipment</option></select><select id="life-state"><option value="">All states</option>'+['quarantine','passed','failed','rma','released','scrapped'].map(s=>'<option>'+s+'</option>').join('')+'</select><button id="life-prev">Previous</button><button id="life-next">Next</button></div><div id="life-list"></div><div id="life-detail"></div>');
 const list=el.querySelector('#life-list'),detail=el.querySelector('#life-detail');
 let view='recoveries',state='',offset=0,selected=null,seq=0;
 const fail=e=>{if(list.isConnected)notice(e.message,true)};
 const opt=(v,l)=>'<option value="'+esc(v)+'">'+esc(l)+'</option>';
 const vals=(...x)=>x.map(v=>esc(v??'—'));
 const table=(headers,rows)=>'<div class="card tablewrap"><table><thead><tr>'+headers.map(h=>'<th>'+esc(h)+'</th>').join('')+'</tr></thead><tbody>'+rows.map(r=>'<tr>'+r.map(v=>'<td>'+v+'</td>').join('')+'</tr>').join('')+'</tbody></table></div>';
 function form(title,fields,save){
  const id=crypto.randomUUID(),d=dialog('<h2>'+esc(title)+'</h2><form>'+fields+'<p class="error"></p><button class="primary">Confirm</button><button type="button" id="cancel">Cancel</button></form>');
  d.querySelector('#cancel').onclick=()=>d.close();
  d.querySelector('form').onsubmit=async e=>{e.preventDefault();const b=e.target.querySelector('.primary');b.disabled=true;try{await save(new FormData(e.target),id);d.close();await load()}catch(err){d.querySelector('.error').textContent=err.message;b.disabled=false}};
 }
 const note='<label>Inspection / custody evidence (no secrets)</label><textarea name="note" minlength="5" maxlength="1000" required rows="5"></textarea>';
 async function recover(asset){
  const [items,warehouses]=await Promise.all([api('/stock/items'),api('/stock/warehouses')]);
  const candidates=items.filter(i=>i.serialized&&i.category===asset.category&&(asset.item_id?i.id===asset.item_id:i.model===asset.model));
  const locations=warehouses.filter(w=>w.branch_id===asset.branch_id&&!w.technician_id);
  if(!candidates.length||!locations.length)throw Error('Matching serialized catalogue item and branch warehouse required');
  form('Recover '+asset.name+' / '+asset.serial_number,'<p>Confirm device was physically recovered and is owned/authorized for return. This retires the deployment record and clears its management IP. No stock is credited until independent release.</p><label>Catalogue item</label><select name="itemId" required>'+candidates.map(i=>opt(i.id,i.sku+' · '+i.name)).join('')+'</select><label>Quarantine custody warehouse</label><select name="warehouseId" required>'+locations.map(w=>opt(w.id,w.name)).join('')+'</select><label>Warranty end (optional, verified manually)</label><input type="date" name="warrantyUntil">'+note,(f,id)=>api('/lifecycle/recoveries','POST',{requestId:id,equipmentId:asset.id,itemId:f.get('itemId'),warehouseId:f.get('warehouseId'),warrantyUntil:f.get('warrantyUntil')||null,note:f.get('note'),confirm:true}));
 }
 async function show(r,skip=0){
  const n=++seq,path='/lifecycle/recoveries/'+r.id,events=await api(path+'/events?offset='+skip);
  if(n!==seq||!detail.isConnected)return;
  const allowed=[];
  if(['quarantine','passed','failed'].includes(r.state))allowed.push(['test_pass','Pass inspection'],['test_fail','Fail inspection']);
  if(r.state==='failed')allowed.push(['send_rma','Send to supplier']);
  if(r.state==='rma')allowed.push(['return_rma','Receive from RMA']);
  if(admin&&r.state==='passed')allowed.push(['release','Release to stock']);
  if(admin&&r.state==='failed')allowed.push(['scrap','Record disposal']);
  detail.innerHTML='<div class="toolbar topgap"><div><h2>'+esc(r.sku)+' · '+esc(r.serial)+'</h2><p>'+esc(r.state)+' · '+esc(r.warehouse)+' · Previous deployment '+esc(r.account||r.highsite||'—')+'</p><p class="sub">Warranty end: '+esc(r.warranty_until||'Not recorded')+' · Condition: '+esc(r.condition||'Not tested')+' · RMA: '+esc(r.rma_reference||'—')+'</p></div><div class="buttons">'+allowed.map(a=>'<button data-action="'+esc(a[0])+'">'+esc(a[1])+'</button>').join('')+'</div></div><div class="toolbar"><h3>Lifecycle history</h3><button id="life-eprev">Previous</button><button id="life-enext">Next</button></div>'+table(['Time','Action','Actor','Note','Supplier / RMA'],events.map(e=>vals(e.occurred_at,e.action,e.username,e.note,[e.supplier,e.rma_reference].filter(Boolean).join(' / '))));
  const prev=detail.querySelector('#life-eprev'),next=detail.querySelector('#life-enext');prev.disabled=skip===0;next.disabled=events.length<100;
  prev.onclick=()=>show(r,Math.max(0,skip-100)).catch(fail);next.onclick=()=>show(r,skip+100).catch(fail);
  detail.querySelectorAll('[data-action]').forEach(b=>b.onclick=async()=>{try{
   const action=b.dataset.action;let fields='';
   if(action==='test_pass')fields='<label>Tested condition</label><select name="condition"><option value="tested_used">Tested used</option><option value="refurbished">Refurbished</option></select>';
   if(action==='send_rma'){const suppliers=(await api('/procurement/suppliers')).filter(s=>s.enabled);fields='<label>Supplier</label><select name="supplierId" required>'+suppliers.map(s=>opt(s.id,s.name)).join('')+'</select><label>RMA reference</label><input name="rmaReference" required minlength="3" maxlength="160">'}
   if(action==='release')fields='<p>Confirm same serial, successful inspection and physical return. The original deployment remains historical.</p>';
   if(action==='scrap')fields='<p>Confirm physical disposal. This does not credit warehouse stock or post an accounting write-off.</p>';
   if(action==='return_rma')fields='<p>Confirm the same serial was physically returned. Replacement serials require separate receiving and are not covered here.</p>';
   form(action.replaceAll('_',' '),fields+note,(f,id)=>api(path+'/action','POST',{requestId:id,action,note:f.get('note'),condition:f.get('condition')||null,supplierId:f.get('supplierId')||null,rmaReference:f.get('rmaReference')||null,confirm:true}));
  }catch(e){fail(e)}});
 }
 async function load(){
  const n=++seq;detail.innerHTML='';const p=new URLSearchParams({offset:String(offset)});if(view==='recoveries'&&state)p.set('state',state);
  const rows=await api('/lifecycle/'+view+'?'+p);if(n!==seq||!list.isConnected)return;
  el.querySelector('#life-state').disabled=view!=='recoveries';el.querySelector('#life-prev').disabled=offset===0;el.querySelector('#life-next').disabled=rows.length<100;
  if(view==='eligible'){
   list.innerHTML=table(['Device','Serial','Customer / site','Dependencies',''],rows.map(r=>[...vals(r.name,r.serial_number,r.account||r.highsite,r.has_dependencies?'Blocked':'None'),r.has_dependencies?'': '<button data-recover="'+esc(r.id)+'">Recover</button>']));
   list.querySelectorAll('[data-recover]').forEach(b=>b.onclick=()=>recover(rows.find(r=>r.id===b.dataset.recover)).catch(fail));
  }else{
   list.innerHTML=table(['SKU','Serial','Previous deployment','Custody','State','Warranty',''],rows.map(r=>[...vals(r.sku,r.serial,r.account||r.highsite,r.state==='rma'?r.supplier:r.warehouse,r.state,r.warranty_until),'<button data-case="'+esc(r.id)+'">Inspect</button>']));
   list.querySelectorAll('[data-case]').forEach(b=>b.onclick=()=>{selected=b.dataset.case;show(rows.find(r=>r.id===selected)).catch(fail)});
   if(selected){const r=rows.find(x=>x.id===selected);if(r)await show(r);else selected=null}
  }
 }
 el.querySelector('#life-view').onchange=e=>{view=e.target.value;offset=0;selected=null;load().catch(fail)};
 el.querySelector('#life-state').onchange=e=>{state=e.target.value;offset=0;selected=null;load().catch(fail)};
 el.querySelector('#life-prev').onclick=()=>{offset=Math.max(0,offset-100);load().catch(fail)};
 el.querySelector('#life-next').onclick=()=>{offset+=100;load().catch(fail)};
 el.querySelector('#life-refresh').onclick=()=>load().catch(fail);
 try{await load()}catch(e){fail(e)}
}

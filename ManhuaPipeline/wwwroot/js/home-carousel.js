document.addEventListener('DOMContentLoaded',()=>{
  const track=document.querySelector('.image-carousel-track');
  const prev=document.querySelector('[data-carousel-prev]');
  const next=document.querySelector('[data-carousel-next]');
  if(!track||!prev||!next)return;
  const step=()=>{const card=track.querySelector('.image-slide');return card?card.getBoundingClientRect().width+12:0};
  const move=direction=>{
    const max=track.scrollWidth-track.clientWidth;
    if(direction>0&&track.scrollLeft>=max-8)track.scrollTo({left:0,behavior:'smooth'});
    else if(direction<0&&track.scrollLeft<=8)track.scrollTo({left:max,behavior:'smooth'});
    else track.scrollBy({left:step()*direction,behavior:'smooth'});
  };
  prev.addEventListener('click',()=>move(-1));
  next.addEventListener('click',()=>move(1));
  let timer=setInterval(()=>move(1),4200);
  track.addEventListener('mouseenter',()=>clearInterval(timer));
  track.addEventListener('mouseleave',()=>{clearInterval(timer);timer=setInterval(()=>move(1),4200)});
});
